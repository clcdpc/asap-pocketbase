import { pb, settingsSectionIds, currentSettingsSection, settingsDirty, settingsSaving, settingsLoading, currentLibraryContextOrgId, currentStatus, setCurrentStatus, setCurrentSettingsSection, setSettingsDirty } from '../state.js';
import { checkSettingsDirty, handleLibraryContextSwitch, refreshLibrarySelectorIndicators } from '../settings.js';
import { loadTab } from '../grid.js';
import { setDisabled } from './dom.js';

export function getSettingsSectionFromHash() {
  const hash = window.location.hash || '';
  const prefix = '#settings-';
  if (!hash.startsWith(prefix)) {
    return '';
  }

  try {
    const section = decodeURIComponent(hash.slice(prefix.length));
    return settingsSectionIds.includes(section) ? section : '';
  } catch (e) {
    return '';
  }
}

export function updateSettingsSaveBarVisibility() {
  const bar = document.querySelector('.settings-save-bar');
  if (!bar) return;
  bar.classList.toggle('hidden', currentStatus !== 'settings');
}

export function activateStatusTab(status) {
  setCurrentStatus(status);
  document.querySelectorAll('#status-tabs .nav-link').forEach(link => {
    const isActive = link.getAttribute('data-status') === status;
    link.classList.toggle('active', isActive);
    if (link.hasAttribute('role')) {
      link.setAttribute('aria-selected', isActive ? 'true' : 'false');
    }
  });
  updateSettingsSaveBarVisibility();
}

export function updateSaveBarState(state) {
  const title = document.getElementById('settings-save-title');
  const detail = document.getElementById('settings-save-detail');
  const warningBadge = document.getElementById('settings-save-warning-badge');
  const discardBtn = document.getElementById('settings-discard-btn');
  const msg = document.getElementById('settings-msg');
  const effectiveState = state || (settingsDirty ? 'dirty' : 'clean');
  const isSystem = currentLibraryContextOrgId === 'system';
  const states = {
    clean: ['No changes', isSystem ? 'System defaults are saved.' : 'Library settings are saved.', 'text-muted'],
    dirty: ['Unsaved changes', isSystem ? 'Save system defaults or discard.' : 'Save library settings or discard.', 'text-warning'],
    saving: ['Saving...', 'Please wait while ASAP applies these settings.', 'text-info'],
    saved: ['Saved', isSystem ? 'System defaults saved successfully.' : 'Library settings saved successfully.', 'text-success'],
    error: ['Error saving', 'Review the message below and try again.', 'text-danger']
  };
  const next = states[effectiveState] || states.clean;
  if (title) title.textContent = next[0];
  if (warningBadge) {
    warningBadge.classList.toggle('hidden', effectiveState !== 'dirty');
  }
  if (detail) {
    detail.textContent = next[1];
    detail.className = 'small ' + next[2];
  }
  if (discardBtn) {
    discardBtn.classList.toggle('hidden', effectiveState !== 'dirty' && effectiveState !== 'error');
    discardBtn.disabled = settingsSaving;
  }
  setDisabled('settings-save-btn', settingsSaving || effectiveState === 'clean');
  if (msg && effectiveState === 'clean') {
    msg.textContent = '';
    msg.className = 'mt-2 font-weight-bold';
  }
}

export function markSettingsDirty() {
  if (settingsLoading || settingsSaving) return;
  const isDirty = checkSettingsDirty();
  setSettingsDirty(isDirty);
  updateSaveBarState(isDirty ? 'dirty' : 'clean');
}

export function markSettingsClean(state = 'clean') {
  setSettingsDirty(false);
  updateSaveBarState(state);
}

export const systemOnlySections = ['start', 'polaris', 'smtp'];
export const libraryOverrideStatusSections = ['workflow', 'patron', 'templates'];
export const libraryContextSections = libraryOverrideStatusSections.concat(['staff']);

export function updateLibraryOverrideStatusVisibility(section, contextOrgId = currentLibraryContextOrgId) {
  const statusAlert = document.getElementById('library-override-status');
  if (!statusAlert) return;

  const targetSection = settingsSectionIds.includes(section) ? section : 'start';
  const isLibraryContext = contextOrgId && contextOrgId !== 'system';
  const shouldShow = isLibraryContext && libraryOverrideStatusSections.includes(targetSection);
  statusAlert.classList.toggle('hidden', !shouldShow);
}

export function activateSettingsSection(section, options = {}) {
  const targetSection = settingsSectionIds.includes(section) ? section : 'start';
  setCurrentSettingsSection(targetSection);

  document.querySelectorAll('[data-settings-section]').forEach(panel => {
    const isActive = panel.getAttribute('data-settings-section') === targetSection;
    panel.classList.toggle('active', isActive);
    panel.setAttribute('aria-hidden', isActive ? 'false' : 'true');
  });

  const wrapper = document.getElementById('library-context-wrapper');
  if (wrapper) {
    if (libraryContextSections.includes(targetSection)) {
      wrapper.classList.remove('hidden');
    } else {
      wrapper.classList.add('hidden');
    }
  }
  updateLibraryOverrideStatusVisibility(targetSection);
  refreshLibrarySelectorIndicators();

  const isLibraryContext = currentLibraryContextOrgId && currentLibraryContextOrgId !== 'system';
  document.querySelectorAll('[data-settings-section]').forEach(panel => {
    const panelSection = panel.getAttribute('data-settings-section');
    const shouldLock = isLibraryContext && systemOnlySections.includes(panelSection);
    panel.classList.toggle('settings-panel-locked', shouldLock);

    let banner = panel.querySelector('.system-only-guard-banner');
    if (shouldLock) {
      if (!banner) {
        banner = document.createElement('div');
        banner.className = 'system-only-guard-banner';
        banner.setAttribute('role', 'status');
        const icon = document.createElement('i');
        icon.className = 'fa fa-lock mr-2';
        banner.appendChild(icon);
        const text = document.createTextNode('These settings are system-wide. ');
        banner.appendChild(text);
        const switchBtn = document.createElement('button');
        switchBtn.type = 'button';
        switchBtn.className = 'btn btn-link btn-sm p-0 font-weight-bold system-only-switch-link';
        switchBtn.textContent = 'Switch to System Level';
        switchBtn.addEventListener('click', async (e) => {
          e.preventDefault();
          await handleLibraryContextSwitch('system');
        });
        banner.appendChild(switchBtn);
        const suffix = document.createTextNode(' to edit.');
        banner.appendChild(suffix);
        const cardBody = panel.querySelector('.card-body');
        if (cardBody) {
          cardBody.insertBefore(banner, cardBody.firstChild);
        }
      }
      panel.querySelectorAll('input, select, textarea').forEach(el => { el.disabled = true; });
      panel.querySelectorAll('.btn:not(.settings-nav-link):not(.system-only-switch-link)').forEach(el => { el.disabled = true; });
    } else {
      if (banner) banner.remove();
      if (panel.classList.contains('active')) {
        panel.querySelectorAll('input, select, textarea').forEach(el => { el.disabled = false; });
        panel.querySelectorAll('.btn:not(.settings-nav-link)').forEach(el => { el.disabled = false; });
      }
    }
  });

  document.querySelectorAll('[data-settings-target]').forEach(button => {
    const isActive = button.getAttribute('data-settings-target') === targetSection;
    button.classList.toggle('active', isActive);
    button.setAttribute('aria-selected', isActive ? 'true' : 'false');
  });

  if (options.updateHash) {
    const nextHash = '#settings-' + encodeURIComponent(targetSection);
    if (window.location.hash !== nextHash) {
      window.history.replaceState(null, '', nextHash);
    }
  }

  if (options.focus) {
    const panel = document.getElementById('settings-' + targetSection);
    if (panel) {
      try {
        panel.focus({ preventScroll: true });
      } catch (err) {
      }
    }
  }
}

export function initSettingsNavigation() {
  document.querySelectorAll('.settings-nav-link[data-settings-target]').forEach(button => {
    const section = button.getAttribute('data-settings-target');
    button.setAttribute('aria-controls', 'settings-' + section);
    button.setAttribute('aria-selected', section === currentSettingsSection ? 'true' : 'false');
    button.addEventListener('click', () => {
      activateSettingsSection(section, { updateHash: true, focus: true });
    });
  });

  window.addEventListener('hashchange', () => {
    const section = getSettingsSectionFromHash();
    if (!section) {
      return;
    }

    if (pb.authStore.isValid && currentStatus !== 'settings') {
      activateStatusTab('settings');
      loadTab('settings');
      return;
    }

    activateSettingsSection(section, { updateHash: false, focus: true });
  });

  activateSettingsSection(getSettingsSectionFromHash() || currentSettingsSection, { updateHash: false });
}
