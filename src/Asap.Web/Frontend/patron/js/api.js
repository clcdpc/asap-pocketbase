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

export function loginPatron(payload) {
  return request('/api/asap/patron/login', {
    method: 'POST',
    validateResponse: data => {
      if (typeof data?.token !== 'string' || !data.token || typeof data?.barcode !== 'string') {
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
  return request('/api/asap/patron/session');
}

export function logoutPatron() {
  return request('/api/asap/patron/logout', {
    method: 'POST',
    allowNoContent: true
  });
}
