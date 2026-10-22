const MAX_RECENT = 8;
const MAX_SQL_ID = 9223372036854775807n;

export function recentStorageKey(staff) {
  const tenant = String(staff?.tenantId || '').toLowerCase();
  const id = String(staff?.id || '');
  const email = String(staff?.authenticationEmail || '').trim().toLowerCase();
  if (!/^[0-9a-f]{8}-[0-9a-f-]{27,}$/.test(tenant) || !validRequestId(id) || !email) return null;
  return `asap.staff.recent.title_request.${tenant}.${id}.${encodeURIComponent(email)}`;
}

export function validRequestId(id) {
  return typeof id === 'string' && /^[1-9]\d{0,18}$/.test(id) && BigInt(id) <= MAX_SQL_ID;
}

export function readRecentRequests(storage, key) {
  if (!key) return [];
  try {
    const raw = JSON.parse(storage.getItem(key) || '[]');
    if (!Array.isArray(raw)) return [];
    const seen = new Set();
    return raw.filter(item => {
      if (!item || item.type !== 'title_request' || !validRequestId(item.id) || seen.has(item.id)) return false;
      seen.add(item.id);
      return true;
    }).slice(0, MAX_RECENT).map(item => ({ type: 'title_request', id: item.id }));
  } catch {
    return [];
  }
}

export function saveRecentRequests(storage, key, items) {
  if (!key) return;
  try {
    storage.setItem(key, JSON.stringify(items.slice(0, MAX_RECENT)));
  } catch {
    // Storage can be unavailable in a private browser session.
  }
}

export function rememberRecentRequest(storage, key, id) {
  if (!validRequestId(id)) return [];
  const items = [{ type: 'title_request', id },
    ...readRecentRequests(storage, key).filter(item => item.id !== id)].slice(0, MAX_RECENT);
  saveRecentRequests(storage, key, items);
  return items;
}

export function forgetRecentRequest(storage, key, id) {
  const items = readRecentRequests(storage, key).filter(item => item.id !== id);
  saveRecentRequests(storage, key, items);
  return items;
}
