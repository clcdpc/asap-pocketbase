import { settingsContainer, settingsLoading, settingsDirty, settingsReloadRequired, settingsSaving, settingsSyncInProgress, settingsActionInProgress, libraryContextLoadSerial, currentLibraryContextOrgId, currentSettingsSection, setSettingsLoading, setSettingsReloadRequired, setAdditionalFieldDefinitions, setCurrentPatronFieldConfig, staffSession, setCurrentLibraryContextOrgId, workflowSettings, organizationsStatus } from '../state.js';
import { setVisible, isSuperAdminStaff, activateSettingsSection, initSettingsNavigation, checkAuth, markSettingsClean, updateSaveBarState, setFieldValue, setFieldChecked, isRequestCanceledError } from '../api.js';
import { updateSaveButtonText } from './save-ui.js';
import { authorizedJson, loadStaffSession } from '../http.js';
import { closeOpenDialogs } from '../dialogs.js';
import { closeActionMenu } from '../grid.js';
import { populateLibrarySelector, loadLibrarySettings } from './library-context.js';
import { updatePublicationOptionsUi } from '../settings-ui.js';
import { syncPolarisOrganizations } from './polaris-sync.js';
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
  const workflow = settings?.workflow || {};
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

function loadLibraryAdminSettings() {
  showSettingsForm();
  updateSaveButtonText();
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
  const polaris = settings?.polaris || {};
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
  if (options.preserveDraft === true &&
      ((settingsDirty && !settingsReloadRequired) || settingsSaving || settingsSyncInProgress || settingsActionInProgress || settingsLoading)) {
    return;
  }
  const startingContextSerial = libraryContextLoadSerial;
  const isSuper = isSuperAdminStaff();
  const showErrors = options.showErrors !== false;
  const guard = settingsLoads.begin('settings');
  let loadFailed = false;
  let settingsLoaded = false;
  let autoSyncPolaris = null;
  setSettingsLoading(true);

  try {
    updateSettingsSidebar(isSuper);
    ensureAllowedSettingsSection(isSuper);
    await loadLibraryContext(isSuper);
    if (!guard.isCurrent() || startingContextSerial !== libraryContextLoadSerial) return;

    const requestedContextOrgId = currentLibraryContextOrgId;
    const loadedLibrarySettings = await loadLibrarySettings(requestedContextOrgId, {
      throwOnError: options.throwOnError === true,
      preserveReloadRequired: options.preserveReloadRequired === true
    });
    if (!guard.isCurrent() || loadedLibrarySettings === undefined || requestedContextOrgId !== currentLibraryContextOrgId) return;
    settingsLoaded = !!loadedLibrarySettings?.version;

    if (!isSuper) {
      loadLibraryAdminSettings();
      return loadedLibrarySettings;
    }

    const polaris = loadedLibrarySettings?.polaris || {};
    if (!options.skipAutoSync) autoSyncPolaris = polaris;
    updateWorkflowSettingsSummary(loadedLibrarySettings);

    populateSystemSettingsForms(loadedLibrarySettings);
    if (!guard.isCurrent() || requestedContextOrgId !== currentLibraryContextOrgId) return;
    showSettingsForm();
    return loadedLibrarySettings;

  } catch (err) {
    loadFailed = true;
    if (guard.isCurrent()) {
      if (settingsLoaded) {
        setSettingsReloadRequired(true);
        updateSaveBarState('reload');
      }
      handleLoadSettingsError(err, showErrors);
    }
    if (options.throwOnError === true) throw err;
  } finally {
    if (guard.isCurrent()) {
      setSettingsLoading(false);
      if (!loadFailed && settingsLoaded) markSettingsClean('clean');
      if (!loadFailed && settingsLoaded && autoSyncPolaris) maybeSyncPolarisOrganizations(autoSyncPolaris);
    }
    settingsLoads.finish('settings', guard.token);
  }
}

export function refreshSettingsView(options = {}) {
  return loadSettings(options);
}

export async function loadStaffConfig() {
  const contextOrgId = currentLibraryContextOrgId;
  const contextSerial = libraryContextLoadSerial;
  try {
    const config = await authorizedJson('/api/asap/config');
    if (contextOrgId !== currentLibraryContextOrgId || contextSerial !== libraryContextLoadSerial) return false;
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
      return true;
    }
  } catch (err) {
    console.error('Failed to load global config');
  }
  return false;
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
