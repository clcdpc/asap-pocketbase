import { pb, currentLibraryContextOrgId, libraryContextLoadSerial, librarySelectorBound, organizationsStatus, organizationsStatusMessage, currentSettingsSection, settingsDirty, workflowSettings, libraryOverridesSummary, setCurrentLibraryContextOrgId, setLibrarySelectorBound, setLibraryOverridesSummary, incrementLibraryContextLoadSerial, setOrganizationsStatus } from '../state.js';
import { isSuperAdminStaff, isPocketBaseAutoCancelError, setVisible, activateSettingsSection, markSettingsClean } from '../api.js';
import { authorizedJson, isAbortError } from '../http.js';
import { showConfirm, showToast } from '../dialogs.js';
import { applyLibrarySettingsToForm } from './form-population.js';
import { rememberLastSavedLibrarySettings, captureSettingsBaseline } from './serialize-save.js';
import { loadStaffAccessSettings } from './staff-access.js';
import { createLatestLoad } from '../../../shared/latest-load.js';

const SUPER_ADMIN_LIBRARY_CONTEXT_STORAGE_KEY = 'asap.superAdmin.settings.libraryContextOrgId';
const librarySettingsLoads = createLatestLoad();

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

    let text = opt.textContent.replace(/ ●$/, '');

    const sections = summary[opt.value] || [];
    if (sections.includes(activeSection)) {
      text += ' ●';
    }

    if (opt.textContent !== text) {
      opt.textContent = text;
      if (opt.value === currentLibraryContextOrgId) {
        const display = document.getElementById('library-context-display');
        if (display) display.textContent = text;
      }
    }
  });
}

export async function populateLibrarySelector() {
  const select = document.getElementById('select-library-context');
  if (!select) return;

  try {
    const savedOrgId = readSavedSuperAdminLibraryContext();
    const selectedOrgId = savedOrgId || currentLibraryContextOrgId || select.value || 'system';
    select.disabled = true;
    const systemOption = document.createElement('option');
    systemOption.value = 'system';
    systemOption.textContent = 'System Defaults';
    select.replaceChildren(systemOption);

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
  activateSettingsSection(currentSettingsSection, { updateHash: false });
  return true;
}

export async function handleLibraryContextSwitch(orgId) {
  const select = document.getElementById('select-library-context');
  return switchLibraryContext(orgId || 'system', select);
}

export async function loadLibrarySettings(orgId) {
  const requestedOrgId = orgId || 'system';
  const guard = librarySettingsLoads.begin('library-settings');
  incrementLibraryContextLoadSerial();
  const requestId = libraryContextLoadSerial;
  setCurrentLibraryContextOrgId(requestedOrgId);

  try {
    let settings = {};

    const result = await authorizedJson(`/api/asap/staff/settings/library?orgId=${encodeURIComponent(requestedOrgId)}&_=${Date.now()}`, {
      cache: 'no-store',
      signal: guard.signal
    });
    if (!guard.isCurrent() || requestId !== libraryContextLoadSerial || requestedOrgId !== currentLibraryContextOrgId) {
      return;
    }

    settings = result;
    rememberLastSavedLibrarySettings(settings);
    applyLibrarySettingsToForm(settings);
    await loadStaffAccessSettings();
    captureSettingsBaseline();
    return settings;

  } catch (err) {
    if (isAbortError(err)) {
      return;
    }
    if (isPocketBaseAutoCancelError(err)) {
      return;
    }
    console.error('Error loading library settings:', err);
    showToast('Failed to load library settings', 'error');
  } finally {
    librarySettingsLoads.finish('library-settings', guard.token);
  }
}
