import { staffSession, staffAccessGeneration, staffSessionEpoch, setStaffSession } from './state.js';
import { HttpError, requestJson, isAbortError } from '../../shared/http.js';

let sessionLoadSerial = 0;

function supersededSessionError() {
  const error = new Error('Staff session request superseded.');
  error.name = 'AbortError';
  return error;
}

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
  const startingEpoch = staffSessionEpoch;
  const serial = ++sessionLoadSerial;
  const ownsSession = () => serial === sessionLoadSerial && startingEpoch === staffSessionEpoch;
  try {
    const session = await requestJson('/api/asap/staff/legacy/session', { cache: 'no-store', signal: options.signal });
    if (!ownsSession()) throw supersededSessionError();
    if (options.apply !== false) setStaffSession(session);
    return session;
  } catch (error) {
    if (!ownsSession()) throw supersededSessionError();
    if (options.apply !== false) applyStaffAccessFailure(error);
    throw error;
  }
}

export async function authorizedJson(path, options = {}) {
  const accessGeneration = staffAccessGeneration;
  const method = String(options.method || 'GET').toUpperCase();
  const headers = { ...(options.headers || {}) };
  if (!['GET', 'HEAD', 'OPTIONS'].includes(method)) {
    if (!headers['X-ASAP-Antiforgery'] && !staffSession.antiforgeryToken) {
      const session = await loadStaffSession({ signal: options.signal });
      if (staffAccessGeneration !== accessGeneration) throw supersededSessionError();
      if (!session.authenticated) {
        throw new HttpError('Your staff session has ended.', 401, { code: 'staff_session_invalid' });
      }
    }
    headers['X-ASAP-Antiforgery'] = headers['X-ASAP-Antiforgery'] || staffSession.antiforgeryToken;
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
