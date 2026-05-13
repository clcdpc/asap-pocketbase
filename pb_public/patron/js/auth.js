import { loginForm, suggestionForm } from './state.js';
import { loginPatron, SessionExpiredError } from './api.js';
import { setAuthToken } from './state.js';
import { applyLoadedUiText, uiConfig } from './config.js';
import { applyUiConfig, updateFormatUI } from './form-ui.js';
import { showLoginStep, showSuggestionStep } from './steps.js';
import { byId, setText, setVisible } from './dom.js';

export function setLoginBusy(isBusy) {
  const btn = byId('login-btn');
  if (!btn) return;
  btn.disabled = Boolean(isBusy);
  btn.textContent = isBusy ? 'Logging in...' : 'Next';
}

export function showLoginError(message) {
  const errorDiv = byId('login-error');
  if (!errorDiv) return;
  errorDiv.textContent = message || 'Incorrect Login - Please try again';
  errorDiv.classList.remove('hidden');
  errorDiv.focus();
}

export function populatePatronIdentity(result, submittedBarcode) {
  setText('display-barcode', submittedBarcode);

  const email = result.email || (result.record && result.record.email);
  if (email) {
    setText('display-email', email);
    setVisible('no-email-msg', false);
  } else {
    setText('display-email', '');
    setText('no-email-msg', uiConfig.noEmailMessage || 'No email is specified on your library account, which means we won\'t be able to send you updates regarding your suggestion. Please contact the library to add an email address to your account if you would like to receive status updates.');
    setVisible('no-email-msg', true);
  }

  if (result.preferredPickupBranchName) {
    setText('display-pickup-branch', result.preferredPickupBranchName);
    setVisible('pickup-branch-container', true);
  } else {
    setVisible('pickup-branch-container', false);
  }
}

export async function handleLoginSubmit(event) {
  event.preventDefault();
  setLoginBusy(true);
  setVisible('login-error', false);

  try {
    const fd = new FormData(loginForm);
    const data = Object.fromEntries(fd.entries());
    const params = new URLSearchParams(window.location.search);
    const orgId = params.get('libraryOrgId') || localStorage.getItem('asap_patron_library_org_id') || '';
    if (orgId) data.libraryOrgId = orgId;

    const result = await loginPatron(data);
    setAuthToken(result.token);

    if (result.record && result.record.libraryOrgId) {
      localStorage.setItem('asap_patron_library_org_id', result.record.libraryOrgId);
    }

    if (result.ui_text) {
      applyLoadedUiText(result);
      applyUiConfig();
    }

    populatePatronIdentity(result, data.username);
    showSuggestionStep();
  } catch (err) {
    setLoginBusy(false);
    showLoginError(err.message || 'Incorrect Login - Please try again');
  }
}

export function logout() {
  setAuthToken('');
  if (loginForm) loginForm.reset();
  if (suggestionForm) suggestionForm.reset();
  setLoginBusy(false);
  setVisible('login-error', false);
  setVisible('submit-error', false);
  updateFormatUI();
  showLoginStep();
}

export function handleSessionExpired(error) {
  if (!(error instanceof SessionExpiredError) && error.status !== 401) return false;
  showLoginError('Your session has expired. Please log in again.');
  showLoginStep();
  return true;
}

export function bindAuthEvents() {
  if (loginForm) loginForm.addEventListener('submit', handleLoginSubmit);

  document.querySelectorAll('.btn-logout').forEach(btn => {
    btn.addEventListener('click', event => {
      event.preventDefault();
      logout();
    });
  });
}
