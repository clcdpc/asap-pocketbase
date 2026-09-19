import { HttpError, isAbortError, requestJson } from '../../shared/http.js';
import { createLatestLoad } from '../../shared/latest-load.js';

let antiforgeryToken = null;
let sessionInvalidHandler = null;
let accessUnavailableHandler = null;

export const latestLoads = createLatestLoad();
export { HttpError, isAbortError };

export function onSessionInvalid(handler) {
  sessionInvalidHandler = handler;
}

export function onAccessUnavailable(handler) {
  accessUnavailableHandler = handler;
}

export async function loadStaffSession(options = {}) {
  const data = await requestJson('/api/asap/staff/session', {
    cache: 'no-store',
    signal: options.signal
  });
  antiforgeryToken = data.antiforgeryToken || null;
  return data;
}

export async function authorizedJson(path, options = {}) {
  const method = String(options.method || 'GET').toUpperCase();
  const headers = { ...(options.headers || {}) };
  if (!['GET', 'HEAD', 'OPTIONS'].includes(method)) {
    if (!antiforgeryToken) {
      const session = await loadStaffSession({ signal: options.signal });
      if (!session.authenticated) {
        const error = new HttpError('Your staff session has ended.', 401, { code: 'staff_session_invalid' });
        if (sessionInvalidHandler) sessionInvalidHandler(error);
        throw error;
      }
    }
    headers['X-ASAP-Antiforgery'] = antiforgeryToken;
  }

  try {
    return await requestJson(path, { ...options, method, headers, cache: 'no-store' });
  } catch (error) {
    if (error && error.status === 401 && sessionInvalidHandler) sessionInvalidHandler(error);
    if (error?.status === 403 && error.response?.accessAllowed === false && accessUnavailableHandler) accessUnavailableHandler(error);
    throw error;
  }
}
