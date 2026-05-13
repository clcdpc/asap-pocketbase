import { authToken, setAuthToken } from './state.js';

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
  const headers = { 'Content-Type': 'application/json' };
  if (authToken) headers.Authorization = authToken;

  const response = await fetch(getApiUrl(path), {
    ...options,
    headers: { ...headers, ...options.headers }
  });

  if (!response.ok) {
    if (response.status === 401 && !path.endsWith('/login')) {
      setAuthToken('');
      throw new SessionExpiredError('Your session has expired. Please log in again.');
    }

    let message = response.statusText;
    let data = null;
    try {
      data = await response.json();
      if (data && data.message) message = data.message;
    } catch (err) {}

    const err = new Error(message);
    err.status = response.status;
    err.response = data;
    throw err;
  }

  return response.json();
}

export function loadPatronConfig(path) {
  return request(path);
}

export function loginPatron(payload) {
  return request('/api/asap/patron/login', {
    method: 'POST',
    body: JSON.stringify(payload)
  });
}

export function submitSuggestion(payload) {
  return request('/api/asap/patron/suggestions', {
    method: 'POST',
    body: JSON.stringify(payload)
  });
}
