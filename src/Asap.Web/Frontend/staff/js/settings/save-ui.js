import { currentLibraryContextOrgId } from '../state.js';

export function updateSaveButtonText() {
  const saveBtn = document.getElementById('settings-save-btn');
  if (saveBtn) {
    saveBtn.textContent = currentLibraryContextOrgId === 'system'
      ? 'Save System Defaults'
      : 'Save Library Settings';
  }
}
