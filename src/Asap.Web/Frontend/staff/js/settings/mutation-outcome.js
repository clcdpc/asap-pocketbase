import { isAbortError } from '../../../shared/http.js';
import { updateSaveBarState } from '../api.js';
import { setSettingsReloadRequired } from '../state.js';
import { showToast } from '../dialogs.js';

export const uncertainMutationMessage = 'The request result could not be confirmed. Reload Settings before making further changes.';

const preCommitRejections = {
  400: new Set(['logo_invalid', 'settings_invalid', 'polaris_organizations_empty', 'organization_invalid',
    'patron_codes_invalid', 'patron_code_unknown']),
  401: new Set(['staff_session_invalid']),
  403: new Set(['staff_scope_forbidden']),
  404: new Set(['organization_not_found', 'format_not_found']),
  409: new Set([
    'stale_version', 'format_referenced', 'system_format_durable', 'format_version_required',
    'settings_version_required', 'invalid_settings_version', 'organization_version_required'
  ]),
  // Sync fetches organization snapshots, and Settings save validates patron codes, before local transactions.
  502: new Set(['polaris_unavailable', 'patron_codes_unavailable'])
};

export function classifyMutationOutcome(error) {
  if (isAbortError(error)) return 'ambiguous';
  const code = error?.response?.code;
  return preCommitRejections[error?.status]?.has(code) ? 'definite_failure' : 'ambiguous';
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
