import { suggestionForm, authToken, setSubmitOutcomeUnknown, submitOutcomeUnknown } from './state.js';
import { submitSuggestion } from './api.js';
import { applySuccessConfig, defaultUiText, uiConfig } from './config.js';
import { renderConflictMessage, renderSuccessMessage } from './form-ui.js';
import { showConflictStep, showSuccessStep } from './steps.js';
import { byId, setText, setVisible } from './dom.js';
import { escapeHtml, sanitizeHtml, applyPatronTextPlaceholders } from './html.js';
import { captureAuthOperation, handleSessionExpired, isCurrentAuthContext } from './auth.js';
import { collectCustomFieldValues } from './custom-fields.js';

export function setSubmitBusy(isBusy) {
  const btn = byId('submit-btn');
  if (!btn) return;
  btn.disabled = Boolean(isBusy || submitOutcomeUnknown);
  btn.textContent = isBusy ? 'Submitting...' : submitOutcomeUnknown ? 'Submission status unknown' : 'Submit';
}

export function showSubmitError(message) {
  const errorDiv = byId('submit-error');
  if (!errorDiv) return;
  errorDiv.textContent = message || 'Error. Please try again';
  errorDiv.classList.remove('hidden');
  errorDiv.focus();
}

export function collectSuggestionPayload() {
  const fd = new FormData(suggestionForm);
  const data = Object.fromEntries(fd.entries());
  const autoholdCheckbox = byId('autohold');
  if (autoholdCheckbox) {
    data.autohold = autoholdCheckbox.checked;
  }
  data.customFields = collectCustomFieldValues();
  return data;
}

export function renderSuccess(result) {
  applySuccessConfig(result);
  renderSuccessMessage();
  showSuccessStep();
}

export function renderConflict(result, fallbackMessage) {
  setText('conflict-title', result.conflictTitle || 'Already Submitted');
  const conflictBody = byId('conflict-body');
  if (conflictBody) {
    const message = result.conflictMessage || (fallbackMessage ? escapeHtml(fallbackMessage) : (uiConfig.alreadySubmittedMessage || defaultUiText.alreadySubmittedMessage));
    // Conflict response HTML is sanitized before rendering.
    conflictBody.innerHTML = sanitizeHtml(applyPatronTextPlaceholders(message, uiConfig));
  } else {
    renderConflictMessage();
  }
  showConflictStep();
}

export async function handleSuggestionSubmit(event) {
  event.preventDefault();
  if (submitOutcomeUnknown) return;
  const pickupSelect = byId('preferred-pickup-branch');
  if (pickupSelect && !pickupSelect.value) {
    showSubmitError('Choose a preferred pickup location before submitting.');
    return;
  }
  const formatSelect = byId('format');
  const availableFormats = Array.isArray(uiConfig.availableFormats)
    ? uiConfig.availableFormats
    : Array.from(formatSelect?.options || []).map(option => option.value);
  const selectedFormat = formatSelect?.value || '';
  if (!selectedFormat || !availableFormats.includes(selectedFormat)) {
    showSubmitError(availableFormats.length === 0
      ? 'No suggestion formats are currently available. Please contact your library.'
      : 'Choose an available material format before submitting.');
    return;
  }
  const operation = captureAuthOperation();
  const token = authToken;
  setSubmitBusy(true);
  setVisible('submit-error', false);

  try {
    const result = await submitSuggestion(collectSuggestionPayload());
    if (!isCurrentAuthContext(operation, token)) return;
    renderSuccess(result);
  } catch (err) {
    if (!isCurrentAuthContext(operation, token)) return;
    if (err.outcomeUnknown) {
      setSubmitOutcomeUnknown(true);
      showSubmitError('We could not confirm whether your suggestion was saved. Please do not submit again; contact your library for help.');
      return;
    }
    if (handleSessionExpired(err)) return;

    if (err.status === 409) {
      renderConflict(err.response || {}, err.message);
      return;
    }

    if (err.status === 406) {
      showSubmitError(err.message || 'You have reached your weekly suggestion limit.');
    } else {
      showSubmitError(err.message || 'Error. Please try again');
    }
  } finally {
    if (isCurrentAuthContext(operation, token)) setSubmitBusy(false);
  }
}

export function bindSubmitEvents() {
  if (suggestionForm) suggestionForm.addEventListener('submit', handleSuggestionSubmit);
}
