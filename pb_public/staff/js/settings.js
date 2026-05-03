import { pb, settingsContainer, settingsForm, formatMap, availableFormats, currentRejectionTemplates, verifiedBibId, publicationOptions, setPublicationOptions, workflowSettings, currentLibraryContextOrgId, lastSavedLibrarySettingsSnapshot, lastSavedLibrarySettingsOrgId, libraryContextLoadSerial, librarySelectorBound, organizationsStatus, setOrganizationsStatus, organizationsStatusMessage, currentSettingsSection, settingsDirty, settingsSaving, settingsLoading, leapBibUrlPattern, lastWorkflowEnabledList, defaultPublicationOptions, defaultAgeGroups, setVerifiedBibId, setCurrentLibraryContextOrgId, setLastSavedLibrarySettingsSnapshot, setLastSavedLibrarySettingsOrgId, setLibrarySelectorBound, setSettingsSaving, setSettingsLoading, setLeapBibUrlPattern, setLastWorkflowEnabledList } from './state.js';
import { setFieldValue, setFieldChecked, getFieldValue, getFieldChecked, validateStaffUrl, normalizeStaffUrl, normalizeLeapBibUrlPattern, isPocketBaseAutoCancelError, validateSmtpHostField, setVisible, showToast, showConfirm, isSuperAdminStaff, closeOpenDialogs, updateSaveBarState, markSettingsDirty, markSettingsClean, activateSettingsSection, initSettingsNavigation, updateEmailStatusBanner, setOrganizationsStatus, checkAuth, loadSetupStatus, authorizedJson, updateAutoRejectEmailControls } from './api.js';
import { closeActionMenu, escapeAttr } from './grid.js';
import { renderEditLeapBibLink } from './modals.js';
import { renderFormatSettings, collectFormatLabels, collectAvailableFormats, updateModalFormatDropdowns } from './settings-formats.js';
import { renderDuplicateStatusLabelSettings, collectDuplicateStatusLabels } from './settings-labels.js';
import { collectSettingsPolaris, syncPolarisOrganizations, renderLibraryParticipationCheckboxes, collectEnabledLibraryIds } from './settings-polaris.js';
import { populateEmailTemplateForms } from './settings-templates.js';
import { setPublicationOptions, setAgeGroups, renderPatronFormatRulesEditor, collectPatronFormatRules, renderOptionListEditor, collectOptionList, addOptionListRow, handleOptionListClick } from './settings-ui.js';
import { loadStaffUsers, populateStaffLibraryOptions } from './settings-users.js';

export function showSettingsAccessDenied() {
  settingsContainer.classList.remove('hidden');
  setVisible('settings-error', true);
  const formEl = document.getElementById('settings-form');
  if (formEl) formEl.classList.add('hidden');
}

export function hideSettingsAccessDenied() {
  setVisible('settings-error', false);
}

export async function loadSettings(options = {}) {
  const isSuper = isSuperAdminStaff();
  const showErrors = options.showErrors !== false;
  setSettingsLoading(true);

  try {
    // Filter sidebar for non-super admins
    document.querySelectorAll('[data-settings-target]').forEach(el => {
      const section = el.getAttribute('data-settings-target');
      // Allow library admins to see settings they can override
      const allowedForAdmins = ['templates', 'workflow', 'patron'];
      if (!isSuper && !allowedForAdmins.includes(section)) {
        el.classList.add('hidden');
      } else {
        el.classList.remove('hidden');
      }
    });

    // If not super admin, force them to an allowed section if they are on a hidden one
    const allowedForAdmins = ['templates', 'workflow', 'patron'];
    if (!isSuper && !allowedForAdmins.includes(currentSettingsSection)) {
      activateSettingsSection('templates', { updateHash: true });
    }

    // Load Library Selector for Super Admins
    if (isSuper) {
      await populateLibrarySelector();
      document.getElementById('super-admin-library-selector').classList.remove('hidden');
    } else {
      document.getElementById('super-admin-library-selector').classList.add('hidden');
      setCurrentLibraryContextOrgId(pb.authStore.model.libraryOrgId || 'system');
      const libraryName = pb.authStore.model.libraryOrgName || 'My Library';
      document.getElementById('library-context-display').textContent = currentLibraryContextOrgId === 'system'
        ? libraryName
        : `${libraryName} (ID ${currentLibraryContextOrgId})`;
    }

    const loadedLibrarySettings = await loadLibrarySettings(currentLibraryContextOrgId);

    if (!isSuper) {
      // Hide error, show form for library admins even if they can't load system-scoped settings.
      hideSettingsAccessDenied();
      const formEl = document.getElementById('settings-form');
      if (formEl) formEl.classList.remove('hidden');
      return;
    }

    const smtp = (loadedLibrarySettings && loadedLibrarySettings.smtp) || {};
    const polaris = (loadedLibrarySettings && loadedLibrarySettings.polaris) || {};
    const emails = (loadedLibrarySettings && loadedLibrarySettings.emails) || {};

    const hasPolarisCredentials = !!(polaris.host && polaris.apiKey && polaris.accessId && polaris.staffDomain && polaris.adminUser && polaris.adminPassword);
    if (hasPolarisCredentials && (setOrganizationsStatus(== 'not_loaded' || organizationsStatus === 'error')) {
      syncPolarisOrganizations().catch(() => {
        // syncPolarisOrganizations updates the visible warning state.
      }));
    }

    workflowSettings.outstandingTimeoutEnabled = !!((loadedLibrarySettings && loadedLibrarySettings.workflow || {}).outstandingTimeoutEnabled);
    workflowSettings.outstandingTimeoutDays = parseInt(((loadedLibrarySettings && loadedLibrarySettings.workflow || {}).outstandingTimeoutDays) || '30', 10) || 30;
    workflowSettings.autoPromote = polaris.autoPromote !== false;

    // Workflow form population is handled by loadLibrarySettings

    // SMTP
    setFieldValue('smtp-host', smtp.host || '');
    setFieldValue('smtp-port', smtp.port || 587);
    setFieldValue('smtp-username', '');
    setFieldValue('smtp-password', '');
    setVisible('smtp-username-status', !!smtp.usernameSet);
    setVisible('smtp-password-status', !!smtp.passwordSet);
    setFieldChecked('smtp-tls', smtp.tls !== false);

    // Also populate the duplicate SMTP fields with the emails value
    setFieldValue('smtp-from', emails.fromAddress || '');
    setFieldValue('smtp-from-name', emails.fromName || '');

    // Polaris
    setFieldValue('polaris-host', polaris.host || '');
    setFieldValue('polaris-api-key', polaris.apiKey || '');
    setFieldValue('polaris-access-id', polaris.accessId || '');
    setFieldValue('polaris-domain', polaris.staffDomain || '');
    setFieldValue('polaris-admin-user', polaris.adminUser || '');
    setFieldValue('polaris-admin-pass', polaris.adminPassword || '');
    setFieldValue('polaris-override-pass', polaris.overridePassword || '');
    setFieldChecked('polaris-auto-promote', polaris.autoPromote !== false);

    // UI Text and Formats are handled by populatePatronUiForms called via loadLibrarySettings
    // but we can set them here if needed. Since loadLibrarySettings is called right before this,
    // it will be populated. Wait, loadLibrarySettings is called AT THE TOP of loadSettings asynchronously!
    // So if it completes before loadSettings finishes, loadSettings might overwrite it?
    // Actually, loadLibrarySettings is awaited at the top. So we should just remove the uiText Population from loadSettings and let loadLibrarySettings handle it.
    await populateStaffLibraryOptions();
    await loadStaffUsers();



    // Success: hide error, show form
    hideSettingsAccessDenied();
    const formEl = document.getElementById('settings-form');
    if (formEl) formEl.classList.remove('hidden');

  } catch (err) {
    if (isPocketBaseAutoCancelError(err)) {
      return;
    }
    console.error('Failed to load settings', err);
    if (showErrors) {
      showSettingsAccessDenied();
    }
  } finally {
    setSettingsLoading(false);
    markSettingsClean('clean');
  }
}

emailTemplateDefaults = {
  suggestion_submitted: {
    subject: 'Suggestion received: {{title}}',
    body: 'Hello {{name}},\n\nThank you for suggesting {{title}} by {{author}} in {{format}} format. Our collection development team has received your request and will review it.\n\nIf we add this item, we will place a hold for you automatically and send another update.\n\nThank you for helping us shape the library collection.'
  },
  already_owned: {
    subject: '{{title}} is already available',
    body: 'Hello {{name}},\n\nThank you for suggesting {{title}} by {{author}} in {{format}} format.\n\nThe library already owns this title or has it on order. We have placed a hold on card {{barcode}} so you will be notified when it is ready.\n\nThank you for using the library\'s suggestion service.'
  },
  rejected: {
    subject: 'Update on your suggestion: {{title}}',
    body: 'Hello {{name}},\n\nThank you for suggesting {{title}} by {{author}} in {{format}} format.\n\nAfter review, we are not able to add this item to the collection at this time. We appreciate you taking the time to share your suggestion with us.\n\nThank you for helping us build a collection that reflects our community.'
  },
  hold_placed: {
    subject: 'Hold placed for {{title}}',
    body: 'Hello {{name}},\n\nGood news. The library plans to add {{title}} by {{author}} in {{format}} format.\n\nWe have placed a hold on card {{barcode}}. You will receive the usual pickup notice when the item is ready.\n\nThank you for your suggestion.'
  }
};

templateFieldIds = [
  'email-submit-subject',
  'email-submit-body',
  'email-owned-subject',
  'email-owned-body',
  'email-rejected-subject',
  'email-rejected-body',
  'email-hold-subject',
  'email-hold-body'
];

export async function populateLibrarySelector() {
  const select = document.getElementById('select-library-context');
  if (!select) return;

  try {
    const selectedOrgId = currentLibraryContextOrgId || select.value || 'system';
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
    setCurrentLibraryContextOrgId(select.value);
    const selectedOption = select.options[select.selectedIndex];
    if (selectedOption) {
      document.getElementById('library-context-display').textContent = selectedOption.text;
    }

    if (!librarySelectorBound) {
      select.addEventListener('change', async (e) => {
        const nextOrgId = e.target.value || 'system';
        const previousOrgId = currentLibraryContextOrgId || 'system';
        if (settingsDirty) {
          const proceed = await showConfirm('Unsaved changes', 'You have unsaved changes. Switch libraries without saving?');
          if (!proceed) {
            e.target.value = previousOrgId;
            return;
          }
        }
        setCurrentLibraryContextOrgId(nextOrgId);
        const display = e.target.options[e.target.selectedIndex].text;
        document.getElementById('library-context-display').textContent = display;
        await loadLibrarySettings(currentLibraryContextOrgId);
        markSettingsClean('clean');
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
  setLeapBibUrlPattern(settings.leapBibUrlPattern || '');

  const resetBtn = document.getElementById('btn-reset-library-settings');
  const statusAlert = document.getElementById('library-override-status');
  const overrideMsg = document.getElementById('library-override-msg');

  if (setCurrentLibraryContextOrgId(== 'system') {
    if (resetBtn) resetBtn.classList.add('hidden'));
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
    if (statusAlert) statusAlert.classList.remove('hidden');
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
  }

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
  setFieldChecked('polaris-auto-promote', polaris.autoPromote !== false);
  const fileInput = document.getElementById('ui-logo-file');
  if (fileInput) fileInput.value = '';

  populateEmailTemplateForms(emails);
  populatePatronUiForms(settings.ui_text || {});
  populateWorkflowForms(settings.workflow || {});
  workflowSettings.outstandingTimeoutEnabled = !!((settings.workflow || {}).outstandingTimeoutEnabled);
  workflowSettings.outstandingTimeoutDays = parseInt(((settings.workflow || {}).outstandingTimeoutDays) || '30', 10) || 30;
  workflowSettings.autoPromote = polaris.autoPromote !== false;
  updateEmailStatusBanner(settings.emailStatus);
  if (settings.organizationSync) {
    const state = settings.organizationSync.status || 'not_loaded';
    const message = settings.organizationSync.error || settings.organizationSync.message || organizationsStatusMessage;
    setOrganizationsStatus(state, message);
  }
}

export function discardLibrarySettingsChanges() {
  if (!lastSavedLibrarySettingsSnapshot || lastSavedLibrarySettingsOrgId !== (currentLibraryContextOrgId || 'system')) return;
  setSettingsLoading(true);
  try {
    applyLibrarySettingsToForm(cloneLibrarySettingsSnapshot(lastSavedLibrarySettingsSnapshot));
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

export async function loadLibrarySettings(orgId) {
  const requestedOrgId = orgId || 'system';
  const requestId = ++libraryContextLoadSerial;
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

  setFieldValue('wf-common-authors-list', wf.commonAuthorsList || '');
  setFieldValue('wf-common-authors-message', wf.commonAuthorsMessage || '');
  document.getElementById('wf-common-authors-enabled').checked = !!wf.commonAuthorsEnabled;
  toggleCommonAuthorsGroup();
}

export function populatePatronUiForms(uiText) {
  setFieldValue('ui-logo-alt', uiText.logoAlt || '');
  setFieldValue('ui-patron-page-title', uiText.pageTitle || '');
  setFieldValue('ui-barcode-label', uiText.barcodeLabel || '');
  setFieldValue('ui-pin-label', uiText.pinLabel || '');
  setFieldValue('ui-login-prompt', uiText.loginPrompt || 'Please enter your information below to start the suggestion process.');
  setFieldValue('ui-login-note', uiText.loginNote || 'Use of this service requires a valid library card. Contact your library if you need assistance with your card or PIN.');
  setFieldValue('ui-suggestion-note', uiText.suggestionFormNote || 'If the library decides to purchase your suggestion, we will automatically place a hold on it and send a confirmation email. Make sure to check your spam folder if you don\'t see the email.');
  setFieldValue('ui-no-email-msg', uiText.noEmailMessage || 'No email is specified on your library account, which means we won\'t be able to send you updates regarding your suggestion. Please contact the library to add an email address to your account if you would like to receive status updates.');
  setFieldValue('ui-success-title', uiText.successTitle || 'Suggestion Submitted');
  setFieldValue('ui-success-msg', uiText.successMessage || 'You have successfully submitted your material suggestion! Check your email inbox for status updates.<div>Thank you for using our suggestion service.</div>');
  setFieldValue('ui-already-submitted-msg', uiText.alreadySubmittedMessage || 'This suggestion has already been submitted from your account. Your previous request was submitted on {{duplicate_date}} and is currently {{duplicate_status}}.<div>Thank you for using this library\'s suggestion service.</div>');
  renderDuplicateStatusLabelSettings(uiText.duplicateStatusLabels || {}, uiText.duplicateStatusLabelsSource || '', !!uiText.duplicateStatusLabelsInherited);
  setFieldValue('ui-ebook-msg', uiText.ebookMessage || '<p>This is an eBook suggestion, please use Libby to notify us of your interest.</p><p><a href="https://help.libbyapp.com/en-us/6260.htm" target="_blank" rel="noreferrer">Learn how to suggest a purchase using Libby here.</a></p>');
  setFieldValue('ui-eaudiobook-msg', uiText.eaudiobookMessage || '<p>This is an eAudiobook suggestion, please use Libby to notify us of your interest.</p><p><a href="https://help.libbyapp.com/en-us/6260.htm" target="_blank" rel="noreferrer">Learn how to suggest a purchase using Libby here.</a></p>');

  // Format Labels & Available Formats
  const labels = uiText.formatLabels || {};
  const available = uiText.availableFormats || ['book', 'audiobook_cd', 'dvd', 'music_cd', 'ebook', 'eaudiobook'];

  // Merge into formatMap for the rest of the app
  Object.keys(labels).forEach(k => formatMap[k] = labels[k]);
  availableFormats = available;

  renderFormatSettings();
  updateModalFormatDropdowns();

  renderOptionListEditor('ui-publication-options-editor', uiText.publicationOptions, defaultPublicationOptions);
  renderOptionListEditor('ui-age-groups-editor', uiText.ageGroups, defaultAgeGroups);
  const patronScope = document.getElementById('patron-options-scope');
  if (patronScope) {
    if (setCurrentLibraryContextOrgId(== 'system') {
      patronScope.textContent = 'Editing global patron form defaults.');
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
  setPublicationOptions(uiText.publicationOptions);
  setAgeGroups(uiText.ageGroups);
}

export function buildSettingsPayload() {
  function positiveInt(id, fallback, label) {
    const raw = getFieldValue(id, String(fallback)).trim();
    if (!raw) return fallback;
    const value = parseInt(raw, 10);
    if (!Number.isFinite(value) || value < 1) {
      throw new Error(`${label} must be a number greater than 0.`);
    }
    return value;
  }

  let staffUrl = '';
  let nextLeapBibUrlPattern = leapBibUrlPattern || '';
  if (isSuperAdminStaff() && setCurrentLibraryContextOrgId(== 'system') {
    staffUrl = getFieldValue('system-staff-url').trim());
    const staffUrlError = validateStaffUrl(staffUrl);
    if (staffUrlError) {
      throw new Error(staffUrlError);
    }
    staffUrl = normalizeStaffUrl(staffUrl);
    setFieldValue('system-staff-url', staffUrl);
    nextLeapBibUrlPattern = normalizeLeapBibUrlPattern(getFieldValue('leap-bib-url-pattern').trim());
    setFieldValue('leap-bib-url-pattern', nextLeapBibUrlPattern);
  }

  const smtp = {
    host: getFieldValue('smtp-host').trim(),
    port: positiveInt('smtp-port', 587, 'SMTP port'),
    username: getFieldValue('smtp-username').trim(),
    password: getFieldValue('smtp-password'),
    tls: getFieldChecked('smtp-tls', true)
  };

  const polaris = collectSettingsPolaris();

  const uiText = {
    logoAlt: getFieldValue('ui-logo-alt'),
    pageTitle: getFieldValue('ui-patron-page-title'),
    barcodeLabel: getFieldValue('ui-barcode-label'),
    pinLabel: getFieldValue('ui-pin-label'),
    loginPrompt: getFieldValue('ui-login-prompt'),
    loginNote: getFieldValue('ui-login-note'),
    suggestionFormNote: getFieldValue('ui-suggestion-note'),
    noEmailMessage: getFieldValue('ui-no-email-msg'),
    successTitle: getFieldValue('ui-success-title'),
    successMessage: getFieldValue('ui-success-msg'),
    alreadySubmittedMessage: getFieldValue('ui-already-submitted-msg'),
    duplicateStatusLabels: collectDuplicateStatusLabels(),
    ebookMessage: getFieldValue('ui-ebook-msg'),
    eaudiobookMessage: getFieldValue('ui-eaudiobook-msg'),
    formatLabels: collectFormatLabels(),
    availableFormats: collectAvailableFormats(),
    publicationOptions: collectOptionList('ui-publication-options-editor', defaultPublicationOptions),
    ageGroups: collectOptionList('ui-age-groups-editor', defaultAgeGroups),
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
    rejection_templates: currentRejectionTemplates,
    hold_placed: {
      subject: getFieldValue('email-hold-subject'),
      body: getFieldValue('email-hold-body')
    }
  };

  const sendAutoRejectEmail = getFieldChecked('outstanding-timeout-send-email');
  const selectedTemplateId = getFieldValue('outstanding-timeout-rejection-template-id');
  const nextAutoRejectTemplateId = selectedTemplateId;
  const assignedTemplateDeleted = workflowSettings.outstandingTimeoutRejectionTemplateId && selectedTemplateId === workflowSettings.outstandingTimeoutRejectionTemplateId && !currentRejectionTemplates.some(t => t.id === workflowSettings.outstandingTimeoutRejectionTemplateId);
  if (getFieldChecked('outstanding-timeout-enabled') && sendAutoRejectEmail) {
    if (assignedTemplateDeleted) {
      throw new Error('This template can’t be deleted because it’s currently used by the auto-reject email. Assign a different template or disable auto-reject before deleting.');
    }
  }

  const payload = {
    smtp, polaris, ui_text: uiText, emails,
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
    enabledLibraryOrgIds: collectEnabledLibraryIds(),
    commonAuthorsEnabled: getFieldChecked('wf-common-authors-enabled'),
    commonAuthorsList: sortAuthorsByLastName(getFieldValue('wf-common-authors-list')),
    commonAuthorsMessage: getFieldValue('wf-common-authors-message')
  };

  if (isSuperAdminStaff() && setCurrentLibraryContextOrgId(== 'system') {
    payload.staffUrl = staffUrl);
    payload.setLeapBibUrlPattern(nextLeapBibUrlPattern);
  }

  const fileInput = document.getElementById('ui-logo-file');
  if (fileInput && fileInput.files.length > 0) {
    payload.logo = fileInput.files[0];
  }

  return payload;
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
    if (!validateSmtpHostField(true)) {
      throw new Error('SMTP host is invalid.');
    }
    const isSuper = isSuperAdminStaff();
    const payload = buildSettingsPayload();

    // Save templates via the new library-scoped API
    const libraryPayload = {
      orgId: currentLibraryContextOrgId,
      staffUrl: payload.staffUrl,
      leapBibUrlPattern: payload.leapBibUrlPattern,
      smtp: payload.smtp,
      polaris: payload.polaris,
      emails: payload.emails,
      ui_text: payload.ui_text,
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
        commonAuthorsList: payload.commonAuthorsList,
        commonAuthorsMessage: payload.commonAuthorsMessage
      }
    };

    const libraryPromise = authorizedJson('/api/asap/staff/settings/library', {
      method: 'POST',
      body: JSON.stringify(libraryPayload)
    });

    await libraryPromise;
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
document.getElementById('ui-age-groups-editor')?.addEventListener('click', handleOptionListClick);
document.getElementById('btn-add-publication-option')?.addEventListener('click', () => addOptionListRow('ui-publication-options-editor', defaultPublicationOptions));
document.getElementById('btn-add-age-group')?.addEventListener('click', () => addOptionListRow('ui-age-groups-editor', defaultAgeGroups));

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
      setPublicationOptions(config.publicationOptions);
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
