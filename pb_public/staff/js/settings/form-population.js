import { setFieldValue, setFieldChecked, setVisible, updateLibraryOverrideStatusVisibility, updateEmailStatusBanner, updateOrganizationsStatusUi, activateSettingsSection, updateAutoRejectEmailControls } from '../api.js';
import { currentLibraryContextOrgId, currentSettingsSection, settingsLoading, formatMap, availableFormats, setAvailableFormats, workflowSettings, lastWorkflowEnabledList, setLastWorkflowEnabledList, defaultPublicationOptions, setCurrentFormatClaimRules, setFormatClaimStaffOptions, setLeapBibUrlPattern, setLeapPatronUrlPattern, leapBibUrlPattern, leapPatronUrlPattern, setAdditionalFieldDefinitions, setCurrentPatronFieldConfig } from '../state.js';
import { toggleTimeoutGroup, toggleHoldPickupTimeoutGroup, togglePendingHoldTimeoutGroup, toggleAdditionalCopyTimeoutGroup, toggleCommonAuthorsGroup } from './toggles.js';
import { renderFormatSettings, updateModalFormatDropdowns } from '../settings-formats.js';
import { renderDuplicateStatusLabelSettings } from './duplicate-labels.js';
import { renderOptionListEditor, renderPatronFormatRulesEditor, updatePublicationOptionsUi } from '../settings-ui.js';
import { populateEmailTemplateForms } from '../settings-templates.js';
import { renderAdditionalFieldsEditor } from '../settings-additional-fields.js';
import { renderLibraryParticipationCheckboxes } from './polaris-fields.js';
import { updateSaveButtonText } from './save-ui.js';

function patronPortalUrl(orgId, embed) {
  const url = new URL('/patron/', window.location.origin);
  if (orgId && orgId !== 'system') url.searchParams.set('libraryOrgId', orgId);
  if (embed) url.searchParams.set('embed', '1');
  return url.toString();
}

function buildIframeEmbedCode(url) {
  return [
    '<iframe',
    '  src="' + url + '"',
    '  title="Suggest a purchase"',
    '  style="width:100%; min-height:720px; border:0;"',
    '  loading="lazy">',
    '</iframe>'
  ].join('\n');
}

function buildLoaderEmbedCode(orgId) {
  return [
    '<div',
    '  data-asap-suggestions',
    '  data-src="' + window.location.origin + '"',
    '  data-library-org-id="' + orgId + '">',
    '</div>',
    '<script src="' + window.location.origin + '/patron/embed.js" async></script>'
  ].join('\n');
}

function updatePatronEmbedSnippet(settings) {
  const isSystem = currentLibraryContextOrgId === 'system';
  setVisible('patron-embed-library-panel', !isSystem);
  setVisible('patron-embed-system-panel', isSystem);
  if (isSystem) return;

  const directUrl = patronPortalUrl(currentLibraryContextOrgId, false);
  const embedUrl = patronPortalUrl(currentLibraryContextOrgId, true);
  setFieldValue('patron-embed-direct-url', directUrl);
  setFieldValue('patron-embed-iframe-code', buildIframeEmbedCode(embedUrl));
  setFieldValue('patron-embed-loader-code', buildLoaderEmbedCode(currentLibraryContextOrgId));

  const warning = document.getElementById('patron-embed-warning');
  if (!warning) return;
  const frameAncestors = String(settings && settings.patronEmbedFrameAncestors || '').trim();
  if (!frameAncestors || frameAncestors === "frame-ancestors 'self'") {
    warning.textContent = 'External embedding is not enabled yet. A super admin must add this library website origin to the system-level allowed patron embed domains.';
    warning.classList.remove('hidden');
  } else {
    warning.textContent = '';
    warning.classList.add('hidden');
  }
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
  setLeapPatronUrlPattern(settings.leapPatronUrlPattern || '');

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
    setFieldValue('leap-patron-url-pattern', leapPatronUrlPattern);
    setFieldValue('format-icon-url-pattern', settings.formatIconUrlPattern || '');
    setFieldValue('patron-embed-allowed-origins', settings.patronEmbedAllowedOrigins || '');
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
      if (overrideMsg) {
        overrideMsg.replaceChildren(
          Object.assign(document.createElement('i'), { className: 'fa fa-check-circle mr-1' }),
          document.createTextNode(' Editing: '),
          Object.assign(document.createElement('strong'), { textContent: document.getElementById('library-context-display').textContent || 'selected library' }),
          document.createTextNode('. This library has custom settings.')
        );
      }
      if (resetBtn) resetBtn.classList.remove('hidden');
    } else {
      if (statusAlert) statusAlert.className = 'alert alert-warning mb-3 d-flex justify-content-between align-items-center';
      if (overrideMsg) {
        overrideMsg.replaceChildren(
          Object.assign(document.createElement('i'), { className: 'fa fa-info-circle mr-1' }),
          document.createTextNode(' Editing: '),
          Object.assign(document.createElement('strong'), { textContent: document.getElementById('library-context-display').textContent || 'selected library' }),
          document.createTextNode('. This library is using system defaults. Saving will create a library-specific override.')
        );
      }
      if (resetBtn) resetBtn.classList.add('hidden');
    }
    updateLibraryOverrideStatusVisibility(currentSettingsSection);
  }
  updatePatronEmbedSnippet(settings);

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
    setFieldValue('polaris-workstation-id', polaris.workstationId || '1');

  }
  const fileInput = document.getElementById('ui-logo-file');
  if (fileInput) fileInput.value = '';

  populateEmailTemplateForms(emails);
  populatePatronUiForms(settings.ui_text || {});
  populateWorkflowForms(settings.workflow || {});
  workflowSettings.isOverride = isOverride;
  workflowSettings.outstandingTimeoutEnabled = !!((settings.workflow || {}).outstandingTimeoutEnabled);
  workflowSettings.outstandingTimeoutDays = parseInt(((settings.workflow || {}).outstandingTimeoutDays) || '30', 10) || 30;
  workflowSettings.additionalCopyTimeoutEnabled = !!((settings.workflow || {}).additionalCopyTimeoutEnabled);
  workflowSettings.additionalCopyTimeoutDays = parseInt(((settings.workflow || {}).additionalCopyTimeoutDays) || '14', 10) || 14;
  workflowSettings.autoPromote = !!(settings.workflow || {}).autoPromote;
  workflowSettings.allowAnyRegisteredCardLogin = !!(settings.workflow || {}).allowAnyRegisteredCardLogin;
    updateEmailStatusBanner(settings.emailStatus);
    if (settings.organizationSync) {
      const state = settings.organizationSync.status || 'not_loaded';
      const message = settings.organizationSync.error || settings.organizationSync.message || '';
      updateOrganizationsStatusUi(state, message);
    }
    updateSaveButtonText();
    if (!settingsLoading) {
      activateSettingsSection(currentSettingsSection, { updateHash: false });
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
  document.getElementById('additional-copy-timeout-enabled').checked = !!wf.additionalCopyTimeoutEnabled;
  setFieldValue('additional-copy-timeout-days', wf.additionalCopyTimeoutDays !== undefined ? wf.additionalCopyTimeoutDays : '14');
  workflowSettings.additionalCopyTimeoutEnabled = !!wf.additionalCopyTimeoutEnabled;
  workflowSettings.additionalCopyTimeoutDays = parseInt(wf.additionalCopyTimeoutDays || '14', 10) || 14;

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
  toggleAdditionalCopyTimeoutGroup();

  setFieldValue('wf-common-authors-label', wf.commonAuthorsLabel || 'Popular Creators');
  setFieldValue('wf-common-authors-help', wf.commonAuthorsHelp || 'See if this is a creator we already collect.');
  setFieldValue('wf-common-authors-list', wf.commonAuthorsList || '');
  setFieldValue('wf-common-authors-message', wf.commonAuthorsMessage || '');
  document.getElementById('wf-common-authors-enabled').checked = !!wf.commonAuthorsEnabled;
  toggleCommonAuthorsGroup();

  setFieldChecked('polaris-auto-promote', !!wf.autoPromote);
  setFieldChecked('allow-patron-autohold-opt-out', !!wf.allowPatronAutoholdOptOut);
  setFieldChecked('allow-any-registered-card-login', !!wf.allowAnyRegisteredCardLogin);
  const orgId = currentLibraryContextOrgId;
  const loginUrl = `${window.location.origin}/patron/?libraryOrgId=${encodeURIComponent(orgId)}`;
  const linkEl = document.getElementById('any-card-login-url');
  if (linkEl) {
    linkEl.href = loginUrl;
    linkEl.textContent = loginUrl;
  }
  workflowSettings.autoPromote = !!wf.autoPromote;
  workflowSettings.allowAnyRegisteredCardLogin = !!wf.allowAnyRegisteredCardLogin;
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

  const fileLabel = document.querySelector('label[for="ui-logo-file"] + .custom-file-label') || document.querySelector('label[for="ui-logo-file"]');
  if (fileLabel) fileLabel.textContent = 'Choose image...';

  setFieldValue('ui-patron-page-title', uiText.pageTitle || '');
  setFieldValue('ui-barcode-label', uiText.barcodeLabel || '');
  setFieldValue('ui-pin-label', uiText.pinLabel || '');
  setFieldValue('ui-login-prompt', uiText.loginPrompt || 'Please enter your information below to start the suggestion process.');
  setFieldValue('ui-login-note', uiText.loginNote || 'Use of this service requires a valid library card. Contact your library if you need assistance with your card or PIN.');
  setFieldValue('ui-suggestion-note', uiText.suggestionFormNote || 'If the library approves your suggestion for purchase, we will email you while it is awaiting ordering and cataloging. Once the item is available in the catalog, we will automatically place a hold when possible and send another update.');
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

  const labels = uiText.formatLabels || {};
  const order = Array.isArray(uiText.formatOrder) ? uiText.formatOrder.filter(k => Object.prototype.hasOwnProperty.call(labels, k)) : [];
  const orderedKeys = order.length ? order.concat(Object.keys(labels).filter(k => order.indexOf(k) < 0)) : Object.keys(labels);
  const available = uiText.availableFormats || ['book', 'audiobook_cd', 'dvd', 'music_cd', 'ebook', 'eaudiobook'];

  Object.keys(formatMap).forEach(k => delete formatMap[k]);
  orderedKeys.forEach(k => { formatMap[k] = labels[k]; });

  availableFormats.length = 0;
  available.forEach(k => availableFormats.push(k));

  renderFormatSettings();
  updateModalFormatDropdowns();

  renderOptionListEditor('ui-publication-options-editor', uiText.publicationOptions, defaultPublicationOptions);
  setAdditionalFieldDefinitions(uiText.additionalFieldDefinitions || []);
  renderAdditionalFieldsEditor(uiText.additionalFieldDefinitions || []);
  setCurrentPatronFieldConfig(uiText.additionalFieldDefinitions || [], uiText.formatRules || {});
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

