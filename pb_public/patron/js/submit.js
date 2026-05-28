import { suggestionForm, patronContextId } from './state.js';
import { submitSuggestion } from './api.js';
import { applySuccessConfig, defaultUiText, uiConfig } from './config.js';
import { renderConflictMessage, renderSuccessMessage } from './form-ui.js';
import { showConflictStep, showSuccessStep } from './steps.js';
import { byId, setText, setVisible } from './dom.js';
import { escapeHtml, sanitizeHtml, applyPatronTextPlaceholders } from './html.js';
import { handleSessionExpired } from './auth.js';

export function setSubmitBusy(isBusy) {
  const btn = byId('submit-btn');
  if (!btn) return;
  btn.disabled = Boolean(isBusy);
  btn.textContent = isBusy ? 'Submitting...' : 'Submit';
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
  const contextId = patronContextId || sessionStorage.getItem('asap_patron_context_id') || '';
  if (contextId) data.patronContextId = contextId;
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
  setSubmitBusy(true);
  setVisible('submit-error', false);

  try {
    renderSuccess(await submitSuggestion(collectSuggestionPayload()));
  } catch (err) {
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
    setSubmitBusy(false);
  }
}

export function bindSubmitEvents() {
  if (suggestionForm) suggestionForm.addEventListener('submit', handleSuggestionSubmit);
}
