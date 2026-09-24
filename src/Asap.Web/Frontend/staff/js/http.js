import { staffSession, staffAccessGeneration, setStaffSession } from './state.js';
import { HttpError, requestJson, isAbortError } from '../../shared/http.js';

const titleRequestVersions = new Map();
const additionalCopyVersions = new Map();
const staffUsers = new Map();

function rememberVersions(value) {
  const rows = Array.isArray(value?.items) ? value.items : [value?.request, value?.additionalCopyRequest, value];
  rows.filter(Boolean).forEach(row => {
    const id = String(row.id || '').trim();
    if (!id || !row.version) return;
    if (row.type === 'additional_copy') additionalCopyVersions.set(id, row.version);
    if (row.type === 'title_request') titleRequestVersions.set(id, row.version);
  });
  (Array.isArray(value?.users) ? value.users : []).forEach(user => {
    staffUsers.set(String(user.id), user);
  });
  return value;
}

function workflowScope(result) {
  const selected = String(result.scope || 'all');
  const superAdmin = staffSession.staff?.role === 'super_admin';
  const libraries = (result.organizations || result.availableLibraries || []).map(item => ({
    orgId: String(item.orgId || item.id || ''),
    name: item.name || item.displayName || ''
  }));
  return {
    ...result,
    availableLibraries: libraries,
    scope: {
      superAdmin,
      mode: selected === 'all' ? 'all' : 'library',
      libraryOrgId: selected === 'all' ? null : selected,
      label: selected === 'all' ? 'All libraries' : (libraries.find(item => item.orgId === selected)?.name || selected)
    }
  };
}

function adaptResponse(path, result) {
  rememberVersions(result);
  if (/^\/api\/asap\/staff\/organizations(?:\?|$)/.test(path) && Array.isArray(result?.data)) {
    return result.data;
  }
  if (/^\/api\/asap\/staff\/polaris\/patron-codes(?:\?|$)/.test(path) && Array.isArray(result?.data)) {
    return result.data;
  }
  if (path === '/api/asap/staff/organizations/sync' && result?.data) {
    return { ...result.data, synced: result.data.changed || 0 };
  }
  if (/\/api\/asap\/staff\/(title-requests|additional-copies)\?/.test(path) && Array.isArray(result?.items)) {
    return workflowScope(result);
  }
  if (/\/api\/asap\/staff\/users(?:\?|$)/.test(path) && Array.isArray(result?.users)) {
    return {
      ...result,
      users: result.users.map(user => ({
        ...user,
        username: user.userPrincipalName,
        libraryOrgId: user.organizationId,
        libraryOrgName: user.organizationId ? `Library ${user.organizationId}` : 'System'
      }))
    };
  }
  return result;
}

function bodyWithVersion(path, body, method) {
  if (['GET', 'HEAD', 'OPTIONS'].includes(method)) return body;
  if (body instanceof FormData || body?.version) return body;
  const value = body && typeof body === 'object' ? body : {};
  const title = path.match(/\/title-requests\/(\d+)(?:\/|$)/) || path.match(/\/requests\/(\d+)(?:\?|$)/);
  if (title) return { ...value, version: titleRequestVersions.get(title[1]) || null };
  const copy = path.match(/\/additional-copies\/(\d+)(?:\/|$)/);
  if (copy) return { ...value, version: additionalCopyVersions.get(copy[1]) || null };
  const user = path.match(/\/staff\/users\/(\d+)(?:\/role)?$/);
  if (user) {
    const existing = staffUsers.get(user[1]);
    return {
      ...value,
      version: existing?.version || null,
      organizationId: value.organizationId ?? (value.role === 'super_admin' ? null : existing?.organizationId)
    };
  }
  if (path === '/api/asap/staff/users') {
    return {
      email: value.email || value.identityKey || value.username,
      role: value.role,
      organizationId: value.organizationId ?? value.libraryOrgId ?? null
    };
  }
  if (path === '/api/asap/staff/profile') return { ...value, version: staffSession.staff?.version || null };
  return body;
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
  try {
    const session = await requestJson('/api/asap/staff/session', { cache: 'no-store', signal: options.signal });
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
      body: bodyWithVersion(path, options.body, method),
      headers,
      cache: 'no-store'
    });
    return staffAccessGeneration === accessGeneration ? adaptResponse(path, result) : result;
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
