import { settingsContainer, settingsLoading, currentLibraryContextOrgId, currentSettingsSection, setSettingsLoading, setAdditionalFieldDefinitions, setCurrentPatronFieldConfig, pb, setCurrentLibraryContextOrgId, workflowSettings, organizationsStatus } from '../state.js';
import { setVisible, isSuperAdminStaff, activateSettingsSection, initSettingsNavigation, checkAuth, loadSetupStatus, markSettingsClean, setFieldValue, setFieldChecked, isPocketBaseAutoCancelError } from '../api.js';
import { updateSaveButtonText } from './form-population.js';
import { authorizedJson } from '../http.js';
import { closeOpenDialogs } from '../dialogs.js';
import { closeActionMenu } from '../grid.js';
import { populateLibrarySelector, loadLibrarySettings } from './library-context.js';
import { updatePublicationOptionsUi } from '../settings-ui.js';
import { syncPolarisOrganizations } from '../settings-polaris.js';
import { loadStaffAccessSettings } from './staff-access.js';
import { registerSettingsRefreshHandlers } from './refresh.js';
import { createLatestLoad } from '../../../shared/latest-load.js';

const adminSettingsSections = ['start', 'staff', 'templates', 'workflow', 'patron'];
const settingsLoads = createLatestLoad();

function maybeSyncPolarisOrganizations(polaris) {
  const hasPolarisCredentials = !!(polaris.host && polaris.apiKey && polaris.accessId && polaris.staffDomain && polaris.adminUser && polaris.adminPassword);
  if (hasPolarisCredentials && (organizationsStatus === 'not_loaded' || organizationsStatus === 'error')) {
    syncPolarisOrganizations().catch(() => {});
  }
}

function updateWorkflowSettingsSummary(settings) {
  const workflow = (settings && settings.workflow) || {};
  workflowSettings.outstandingTimeoutEnabled = !!workflow.outstandingTimeoutEnabled;
  workflowSettings.outstandingTimeoutDays = parseInt(workflow.outstandingTimeoutDays || '30', 10) || 30;
  workflowSettings.additionalCopyTimeoutEnabled = !!workflow.additionalCopyTimeoutEnabled;
  workflowSettings.additionalCopyTimeoutDays = parseInt(workflow.additionalCopyTimeoutDays || '14', 10) || 14;
  workflowSettings.autoPromote = !!workflow.autoPromote;
  workflowSettings.allowAnyRegisteredCardLogin = !!workflow.allowAnyRegisteredCardLogin;
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
  showSettingsForm();
  await loadStaffAccessSettings();
  updateSaveButtonText();
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

export function showSettingsAccessDenied() {
  settingsContainer.classList.remove('hidden');
  setVisible('settings-error', true);
  const formEl = document.getElementById('settings-form');
  if (formEl) formEl.classList.add('hidden');
}

export function hideSettingsAccessDenied() {
  setVisible('settings-error', false);
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
  setFieldValue('polaris-workstation-id', polaris.workstationId || '1');
}

export async function loadSettings(options = {}) {
  const isSuper = isSuperAdminStaff();
  const showErrors = options.showErrors !== false;
  const guard = settingsLoads.begin('settings');
  setSettingsLoading(true);

  try {
    updateSettingsSidebar(isSuper);
    ensureAllowedSettingsSection(isSuper);
    await loadLibraryContext(isSuper);
    if (!guard.isCurrent()) return;

    const loadedLibrarySettings = await loadLibrarySettings(currentLibraryContextOrgId);
    if (!guard.isCurrent()) return;

    if (!isSuper) {
      await loadLibraryAdminSettings();
      return;
    }

    const polaris = (loadedLibrarySettings && loadedLibrarySettings.polaris) || {};
    maybeSyncPolarisOrganizations(polaris);
    updateWorkflowSettingsSummary(loadedLibrarySettings);

    populateSystemSettingsForms(loadedLibrarySettings);
    await loadStaffAccessSettings();
    if (!guard.isCurrent()) return;
    showSettingsForm();

  } catch (err) {
    if (guard.isCurrent()) {
      handleLoadSettingsError(err, showErrors);
    }
  } finally {
    if (guard.isCurrent()) {
      setSettingsLoading(false);
      markSettingsClean('clean');
    }
    settingsLoads.finish('settings', guard.token);
  }
}

export function refreshSettingsView(options = {}) {
  return loadSettings(options);
}

export async function loadStaffConfig() {
  try {
    const config = await authorizedJson('/api/asap/config');
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
      if (currentLibraryContextOrgId === 'system') {
        setAdditionalFieldDefinitions(config.additionalFieldDefinitions || []);
        setCurrentPatronFieldConfig(config.additionalFieldDefinitions || [], config.formatRules || {});
      }
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

registerSettingsRefreshHandlers({ refreshSettingsView, loadStaffConfig });
