import { isAbortError } from '../../../shared/http.js';
import { updateSaveBarState } from '../api.js';
import { setSettingsReloadRequired } from '../state.js';
import { showToast } from '../dialogs.js';

export const uncertainMutationMessage = 'The request result could not be confirmed. Reload Settings before making further changes.';

export function classifyMutationOutcome(error) {
  if (isAbortError(error)) return 'ambiguous';
  return error?.response?.operationPhase === 'rejected' || [401, 403].includes(error?.status)
    ? 'definite_failure' : 'ambiguous';
}

export function isAmbiguousMutationError(error) {
  return classifyMutationOutcome(error) === 'ambiguous';
}

export function markAmbiguousSettingsMutation(error, contextIsCurrent) {
  if (!contextIsCurrent() || !isAmbiguousMutationError(error)) return false;
  setSettingsReloadRequired(true);
  updateSaveBarState('reload');
  const message = document.getElementById('settings-msg');
  if (message) {
    message.textContent = uncertainMutationMessage;
    message.className = 'mt-2 font-weight-bold text-warning';
  }
  showToast(uncertainMutationMessage, 'error', 'settings-save-toast');
  return true;
}
