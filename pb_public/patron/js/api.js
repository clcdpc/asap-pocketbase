import { authToken, setAuthToken } from './state.js';
import { requestJson } from '../../shared/http.js';

export class SessionExpiredError extends Error {
  constructor(message) {
    super(message);
    this.name = 'SessionExpiredError';
    this.status = 401;
  }
}

export function getApiUrl(path) {
  return window.location.origin + path;
}

export async function request(path, options = {}) {
  const headers = {};
  if (authToken) headers.Authorization = authToken;

  try {
    return await requestJson(getApiUrl(path), {
      ...options,
      headers: { ...headers, ...(options.headers || {}) }
    });
  } catch (err) {
    if (err.status === 401 && !path.endsWith('/login')) {
      setAuthToken('');
      throw new SessionExpiredError('Your session has expired. Please log in again.');
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
    body: payload
  });
}

export function submitSuggestion(payload) {
  return request('/api/asap/patron/suggestions', {
    method: 'POST',
    body: payload
  });
}
