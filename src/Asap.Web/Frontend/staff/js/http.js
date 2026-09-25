import { staffSession, staffAccessGeneration, setStaffSession } from './state.js';
import { HttpError, requestJson, isAbortError } from '../../shared/http.js';

function applyStaffAccessFailure(error) {
  if (error?.status === 401) {
    setStaffSession({
      authenticated: false,
      code: error.response?.code || 'staff_session_invalid',
      antiforgeryToken: staffSession.antiforgeryToken
    });
    window.dispatchEvent(new CustomEvent('asap:session-invalid'));
    return true;
  }

  if (error?.status === 403 && error.response?.accessAllowed === false) {
    setStaffSession({
      authenticated: true,
      accessAllowed: false,
      code: error.response?.code || 'staff_scope_forbidden',
      antiforgeryToken: staffSession.antiforgeryToken
    });
    window.dispatchEvent(new CustomEvent('asap:access-forbidden', { detail: error.response }));
    return true;
  }

  return false;
}

export async function loadStaffSession(options = {}) {
  try {
    const session = await requestJson('/api/asap/staff/legacy/session', { cache: 'no-store', signal: options.signal });
    setStaffSession(session);
    return session;
  } catch (error) {
    applyStaffAccessFailure(error);
    throw error;
  }
}

export async function authorizedJson(path, options = {}) {
  let accessGeneration = staffAccessGeneration;
  const method = String(options.method || 'GET').toUpperCase();
  const headers = { ...(options.headers || {}) };
  if (!['GET', 'HEAD', 'OPTIONS'].includes(method)) {
    if (!staffSession.antiforgeryToken) {
      const session = await loadStaffSession({ signal: options.signal });
      if (!session.authenticated) {
        throw new HttpError('Your staff session has ended.', 401, { code: 'staff_session_invalid' });
      }
      accessGeneration = staffAccessGeneration;
    }
    headers['X-ASAP-Antiforgery'] = staffSession.antiforgeryToken;
  }
  try {
    const result = await requestJson(path, {
      ...options,
      method,
      headers,
      cache: 'no-store'
    });
    return result;
  } catch (error) {
    if (staffAccessGeneration !== accessGeneration) {
      throw error;
    }
    if (!applyStaffAccessFailure(error) && error?.status === 409) {
      window.dispatchEvent(new CustomEvent('asap:stale-write', { detail: error.response }));
    }
    throw error;
  }
}

export { isAbortError };
