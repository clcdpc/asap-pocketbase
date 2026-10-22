import { HttpError, isAbortError, requestJson } from '../../shared/http.js';
import { actorKey } from './session-identity.js';

let antiforgeryToken = null;
let sessionInvalidHandler = null;
let accessUnavailableHandler = null;
let sessionContext = null;
let sessionInvalidated = false;

export { HttpError, isAbortError };

export function onSessionInvalid(handler) {
  sessionInvalidHandler = handler;
  return () => { if (sessionInvalidHandler === handler) sessionInvalidHandler = null; };
}

export function onAccessUnavailable(handler) {
  accessUnavailableHandler = handler;
  return () => { if (accessUnavailableHandler === handler) accessUnavailableHandler = null; };
}

export async function loadStaffSession(options = {}) {
  if (sessionInvalidated) {
    throw new HttpError('The staff account or access changed. Reload this page to continue.', 401,
      { code: 'staff_session_changed' });
  }
  const data = await requestJson('/api/asap/staff/session', {
    cache: 'no-store',
    signal: options.signal
  });
  options.signal?.throwIfAborted();
  if (sessionInvalidated) {
    throw new HttpError('The staff account or access changed. Reload this page to continue.', 401,
      { code: 'staff_session_changed' });
  }
  const nextContext = data.authenticated && data.accessAllowed && data.staff?.tenantId && data.staff?.id
    ? actorKey(data.staff)
    : null;
  if (sessionContext && nextContext !== sessionContext) {
    sessionInvalidated = true;
    sessionContext = null;
    antiforgeryToken = null;
    const error = new HttpError('The staff account or access changed. Reload this page to continue.', 401,
      { code: 'staff_session_changed' });
    sessionInvalidHandler?.(error);
    throw error;
  }
  sessionContext = nextContext;
  antiforgeryToken = data.antiforgeryToken || null;
  return data;
}

export async function authorizedJson(path, options = {}) {
  const method = String(options.method || 'GET').toUpperCase();
  const headers = { ...(options.headers || {}) };
  try {
    if (sessionContext) await loadStaffSession({ signal: options.signal });
    if (!['GET', 'HEAD', 'OPTIONS'].includes(method)) {
      if (!antiforgeryToken) {
        const session = await loadStaffSession({ signal: options.signal });
        if (!session.authenticated) {
          throw new HttpError('Your staff session has ended.', 401, { code: 'staff_session_invalid' });
        }
      }
      headers['X-ASAP-Antiforgery'] = antiforgeryToken;
    }

    const result = await requestJson(path, { ...options, method, headers, cache: 'no-store' });
    options.signal?.throwIfAborted();
    if (method === 'GET' && sessionContext) await loadStaffSession({ signal: options.signal });
    return result;
  } catch (error) {
    options.signal?.throwIfAborted();
    if (error && error.status === 401 && error.response?.code !== 'staff_session_changed' &&
        sessionInvalidHandler) sessionInvalidHandler(error);
    if (error?.status === 403 && error.response?.accessAllowed === false && accessUnavailableHandler) accessUnavailableHandler(error);
    throw error;
  }
}
