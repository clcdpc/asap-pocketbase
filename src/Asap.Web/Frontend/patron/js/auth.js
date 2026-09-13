import { loginForm, suggestionForm } from './state.js';
import { loginPatron, logoutPatron, restorePatronSession, SessionExpiredError } from './api.js';
import { authToken, setAuthToken } from './state.js';
import { applyLoadedUiText, uiConfig } from './config.js';
import { applyUiConfig, updateFormatUI } from './form-ui.js';
import { showLoginStep, showSuggestionStep } from './steps.js';
import { byId, setText, setVisible, setDisabled } from './dom.js';
import { applyPatronTextPlaceholders } from './html.js';

let authOperation = 0;

function beginAuthOperation() {
  authOperation += 1;
  return authOperation;
}

function isCurrentAuthOperation(operation) {
  return operation === authOperation;
}

export function captureAuthOperation() {
  return authOperation;
}

export function patronContextCookieValue() {
  const cookie = document.cookie || '';
  const prefix = 'asap_patron_library_org_id=';
  const parts = cookie.split(';').map(part => part.trim());
  for (const part of parts) {
    if (part.indexOf(prefix) === 0) return decodeURIComponent(part.slice(prefix.length));
  }
  return '';
}

export function getPatronExperienceLibraryOrgId() {
  const params = new URLSearchParams(window.location.search);
  return params.get('libraryOrgId') || patronContextCookieValue() || localStorage.getItem('asap_patron_library_org_id') || '';
}

export function storePatronExperienceLibraryOrgId(orgId) {
  const clean = String(orgId || '').trim();
  if (!clean) return;
  localStorage.setItem('asap_patron_library_org_id', clean);
}

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
    setText('no-email-msg', applyPatronTextPlaceholders(uiConfig.noEmailMessage || 'No email is specified on your library account, which means we won\'t be able to send you updates regarding your suggestion. Please contact the library to add an email address to your account if you would like to receive status updates.', uiConfig));
    setVisible('no-email-msg', true);
  }

  populatePickupSelector(result);
}

function populatePickupSelector(result) {
  const container = byId('pickup-branch-container');
  const select = byId('preferred-pickup-branch');
  const warning = byId('pickup-branch-warning');
  const submitBtn = byId('submit-btn');
  if (!container || !select) return;

  const branches = Array.isArray(result.pickupBranches) ? result.pickupBranches : [];
  const selectedId = String(result.selectedPickupBranchId || '');
  const warningText = String(result.pickupBranchWarning || '');

  select.replaceChildren();
  const blank = document.createElement('option');
  blank.value = '';
  blank.textContent = 'Select a pickup location...';
  select.appendChild(blank);

  branches.forEach((branch) => {
    const option = document.createElement('option');
    option.value = String(branch.id || '');
    option.textContent = String(branch.label || branch.name || branch.id || '');
    select.appendChild(option);
  });

  select.value = selectedId;
  setVisible(container, true);
  setDisabled(select, branches.length === 0);

  const hasValidSelection = !!select.value;
  if (warning) {
    const message = warningText || (branches.length === 0 ? 'No pickup locations are currently available for this account. Please contact staff.' : '');
    warning.textContent = message;
    warning.classList.toggle('hidden', !message);
  }
  if (submitBtn) {
    submitBtn.disabled = !hasValidSelection || branches.length === 0;
  }

  if (!select.dataset.pickupBound) {
    select.addEventListener('change', () => {
      const ok = !!select.value;
      if (submitBtn) submitBtn.disabled = !ok;
    });
    select.dataset.pickupBound = 'true';
  }
}

export async function handleLoginSubmit(event) {
  event.preventDefault();
  const operation = beginAuthOperation();
  setLoginBusy(true);
  setVisible('login-error', false);

  try {
    const fd = new FormData(loginForm);
    const data = Object.fromEntries(fd.entries());
    const orgId = getPatronExperienceLibraryOrgId();
    if (orgId) data.libraryOrgId = orgId;

    const result = await loginPatron(data);
    if (!isCurrentAuthOperation(operation)) return;
    setAuthToken(result.token);

    storePatronExperienceLibraryOrgId(result.effectiveLibraryOrgId || (result.record && result.record.libraryOrgId));

    if (result.ui_text) {
      applyLoadedUiText(result);
      applyUiConfig();
    }

    populatePatronIdentity(result, data.username);
    showSuggestionStep();
  } catch (err) {
    if (!isCurrentAuthOperation(operation)) return;
    setLoginBusy(false);
    showLoginError(err.message || 'Incorrect Login - Please try again');
  }
}

export async function logout() {
  const operation = beginAuthOperation();
  try {
    if (authToken) await logoutPatron();
  } catch (err) {
    console.error('Server logout failed', err);
  } finally {
    if (isCurrentAuthOperation(operation)) clearPatronState();
  }
}

function clearPatronState() {
  setAuthToken('');
  if (loginForm) loginForm.reset();
  if (suggestionForm) suggestionForm.reset();
  setLoginBusy(false);
  setVisible('login-error', false);
  setVisible('submit-error', false);
  updateFormatUI();
  showLoginStep();
}

export async function restoreSession(expectedOperation = authOperation) {
  if (!authToken || authOperation !== expectedOperation) return;
  const operation = beginAuthOperation();
  const restoredToken = authToken;
  try {
    const result = await restorePatronSession();
    if (!isCurrentAuthOperation(operation) || authToken !== restoredToken) return;
    if (result.ui_text) {
      applyLoadedUiText(result);
      applyUiConfig();
    }
    populatePatronIdentity(result, result.barcode || '');
    showSuggestionStep();
  } catch (err) {
    if (!isCurrentAuthOperation(operation) || (authToken && authToken !== restoredToken)) return;
    clearPatronState();
    if (err.status !== 401) {
      showLoginError(err.message || 'Current patron information could not be loaded.');
    }
  }
}

export function handleSessionExpired(error) {
  if (!(error instanceof SessionExpiredError) && error.status !== 401) return false;
  if (error.requestToken && authToken && error.requestToken !== authToken) return true;
  showLoginError('Your session has expired. Please log in again.');
  showLoginStep();
  return true;
}

export function bindAuthEvents() {
  if (loginForm) loginForm.addEventListener('submit', handleLoginSubmit);

  document.querySelectorAll('.btn-logout').forEach(btn => {
    btn.addEventListener('click', async event => {
      event.preventDefault();
      await logout();
    });
  });
}
