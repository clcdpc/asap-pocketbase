import { currentLibraryContextOrgId } from './state.js';
import { markSettingsClean } from './api.js';
import { authorizedJson } from './http.js';
import { showToast, showConfirm } from './dialogs.js';
import { loadLibrarySettings } from './settings/library-context.js';
import { normalizeDuplicateStatusLabels, renderDuplicateStatusLabelSettings, collectDuplicateStatusLabels } from './settings/duplicate-labels.js';

export { normalizeDuplicateStatusLabels, renderDuplicateStatusLabelSettings, collectDuplicateStatusLabels };

document.getElementById('btn-reset-library-settings').addEventListener('click', async () => {
  if (currentLibraryContextOrgId === 'system') return;
  const confirmed = await showConfirm('Reset library settings', 'Are you sure you want to delete this library\'s overrides and revert to system defaults?');
  if (confirmed) {
    await authorizedJson('/api/asap/staff/settings/library', {
      method: 'POST',
      body: { orgId: currentLibraryContextOrgId, action: 'reset' }
    });
    showToast('Library settings reset to system defaults', 'success');
    await loadLibrarySettings(currentLibraryContextOrgId);
    markSettingsClean('clean');
  }
});
