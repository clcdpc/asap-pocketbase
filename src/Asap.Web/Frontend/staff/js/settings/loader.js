import { settingsContainer, settingsLoading, currentLibraryContextOrgId, currentSettingsSection, setSettingsLoading, setAdditionalFieldDefinitions, setCurrentPatronFieldConfig, staffSession, setCurrentLibraryContextOrgId, workflowSettings, organizationsStatus } from '../state.js';
import { setVisible, isSuperAdminStaff, activateSettingsSection, initSettingsNavigation, checkAuth, markSettingsClean, setFieldValue, setFieldChecked, isRequestCanceledError } from '../api.js';
import { updateSaveButtonText } from './save-ui.js';
import { authorizedJson, loadStaffSession } from '../http.js';
import { closeOpenDialogs } from '../dialogs.js';
import { closeActionMenu } from '../grid.js';
import { populateLibrarySelector, loadLibrarySettings } from './library-context.js';
import { updatePublicationOptionsUi } from '../settings-ui.js';
import { syncPolarisOrganizations } from './polaris-sync.js';
import { loadStaffAccessSettings } from './staff-access.js';
import { registerSettingsRefreshHandlers } from './refresh.js';
import { createLatestLoad } from '../../../shared/latest-load.js';
import { isPolarisConfigured, populatePolarisSettingsForm } from './polaris-fields.js';

const adminSettingsSections = ['start', 'smtp', 'staff', 'templates', 'workflow', 'patron'];
const settingsLoads = createLatestLoad();

function maybeSyncPolarisOrganizations(polaris) {
  if (isPolarisConfigured(polaris) && (organizationsStatus === 'not_loaded' || organizationsStatus === 'error')) {
    syncPolarisOrganizations().catch(() => {});
  }
}

function updateWorkflowSettingsSummary(settings) {
  const workflow = (currentLibraryContextOrgId === 'system' && settings && settings.stored && settings.stored.workflow) ||
    (settings && settings.workflow) || {};
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
  setCurrentLibraryContextOrgId(staffSession.staff?.libraryOrgId || 'system');
  const libraryName = staffSession.staff?.libraryOrgName || 'My Library';
  document.getElementById('library-context-display').textContent = currentLibraryContextOrgId === 'system'
    ? libraryName
    : `${libraryName} (ID ${currentLibraryContextOrgId})`;
}

async function loadLibraryAdminSettings(options = {}) {
  showSettingsForm();
  const loaded = await loadStaffAccessSettings(options);
  if (loaded && (!options.isCurrent || options.isCurrent())) {
    updateSaveButtonText();
  }
  return loaded;
}

function showSettingsForm() {
  hideSettingsAccessDenied();
  const formEl = document.getElementById('settings-form');
  if (formEl) formEl.classList.remove('hidden');
}

function handleLoadSettingsError(err, showErrors) {
  if (isRequestCanceledError(err)) {
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
  const polaris = (settings && settings.stored && settings.stored.polaris) || (settings && settings.polaris) || {};
  const emails = (settings && settings.emails) || {};

  populatePostmarkSettingsForm(emails);
  populatePolarisSettingsForm(polaris);
}

function populatePostmarkSettingsForm(emails) {
  setFieldValue('postmark-token', '');
  setFieldChecked('postmark-clear-token', false);
  setVisible('postmark-token-status', !!emails.hasPostmarkToken);

  setFieldValue('smtp-from', emails.fromAddress || '');
  setFieldValue('smtp-from-name', emails.fromName || '');
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

    const requestedContextOrgId = currentLibraryContextOrgId;
    const loadedLibrarySettings = await loadLibrarySettings(requestedContextOrgId);
    if (!guard.isCurrent() || loadedLibrarySettings === undefined || requestedContextOrgId !== currentLibraryContextOrgId) return;

    if (!isSuper) {
      await loadLibraryAdminSettings({
        contextOrgId: requestedContextOrgId,
        signal: guard.signal,
        isCurrent: guard.isCurrent
      });
      return;
    }

    const polaris = (loadedLibrarySettings && loadedLibrarySettings.stored && loadedLibrarySettings.stored.polaris) ||
      (loadedLibrarySettings && loadedLibrarySettings.polaris) || {};
    maybeSyncPolarisOrganizations(polaris);
    updateWorkflowSettingsSummary(loadedLibrarySettings);

    populateSystemSettingsForms(loadedLibrarySettings);
    await loadStaffAccessSettings({
      contextOrgId: requestedContextOrgId,
      signal: guard.signal,
      isCurrent: guard.isCurrent
    });
    if (!guard.isCurrent() || requestedContextOrgId !== currentLibraryContextOrgId) return;
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
        document.getElementById('login-logo').src = config.logoUrl;
        document.getElementById('nav-logo').src = config.logoUrl;
      }
      if (config.logoAlt) {
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
  try {
    await loadStaffSession();
  } catch (error) {
    const expectedSessionFailure = error?.status === 401 ||
      (error?.status === 403 && error.response?.accessAllowed === false);
    if (!expectedSessionFailure) {
      throw error;
    }
  }
  checkAuth();
}

registerSettingsRefreshHandlers({ refreshSettingsView, loadStaffConfig });
