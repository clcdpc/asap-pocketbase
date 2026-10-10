import { authToken } from './state.js';
import { requestJson } from '../../shared/http.js';

export class SessionExpiredError extends Error {
  constructor(message, requestToken) {
    super(message);
    this.name = 'SessionExpiredError';
    this.status = 401;
    this.requestToken = requestToken || '';
  }
}

export function getApiUrl(path) {
  return window.location.origin + path;
}

export async function request(path, options = {}) {
  const requestToken = authToken;
  const headers = {};
  if (requestToken) headers.Authorization = 'Bearer ' + requestToken;

  try {
    return await requestJson(getApiUrl(path), {
      ...options,
      requireObjectResponse: true,
      headers: { ...headers, ...(options.headers || {}) }
    });
  } catch (err) {
    if (err.status === 401 && !path.endsWith('/login')) {
      throw new SessionExpiredError('Your session has expired. Please log in again.', requestToken);
    }
    throw err;
  }
}

export function loadPatronConfig(path) {
  return request(path);
}

function isPositiveInt32(value) {
  return Number.isInteger(value) && value > 0 && value <= 2147483647;
}

function hasValidPatronSession(data, requireToken) {
  const tokenIsValid = !requireToken ||
    typeof data?.token === 'string' && data.token.trim().length > 0;
  const selectedBranchIsValid = !Object.prototype.hasOwnProperty.call(data || {}, 'selectedPickupBranchId') ||
    data.selectedPickupBranchId === null || isPositiveInt32(data.selectedPickupBranchId);

  return tokenIsValid &&
    typeof data?.barcode === 'string' && data.barcode.trim().length > 0 &&
    Number.isInteger(data?.effectiveLibraryOrgId) && data.effectiveLibraryOrgId > 1 &&
    data.effectiveLibraryOrgId <= 2147483647 &&
    Array.isArray(data?.pickupBranches) &&
    data.pickupBranches.every(branch => branch && typeof branch === 'object' && !Array.isArray(branch) &&
      isPositiveInt32(branch.id) && typeof branch.label === 'string') &&
    selectedBranchIsValid;
}

export function loginPatron(payload) {
  return request('/api/asap/patron/login', {
    method: 'POST',
    validateResponse: data => {
      if (!hasValidPatronSession(data, true)) {
        throw new Error('Login response is incomplete.');
      }
    },
    body: payload
  });
}

export function submitSuggestion(payload) {
  return request('/api/asap/patron/suggestions', {
    method: 'POST',
    validateResponse: data => {
      if (typeof data?.id !== 'string' || !/^[0-9]+$/.test(data.id) ||
          typeof data?.successTitle !== 'string' || typeof data?.successMessage !== 'string') {
        throw new Error('Suggestion response is incomplete.');
      }
    },
    body: payload
  });
}

export function restorePatronSession() {
  return request('/api/asap/patron/session', {
    validateResponse: data => {
      if (!hasValidPatronSession(data, false)) {
        throw new Error('Current patron information is incomplete.');
      }
    }
  });
}

export function logoutPatron() {
  return request('/api/asap/patron/logout', {
    method: 'POST',
    allowNoContent: true
  });
}
