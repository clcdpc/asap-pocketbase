import { pb } from './state.js';

export async function authorizedJson(path, options = {}) {
  const headers = { ...(options.headers || {}) };
  if (pb.authStore.token) {
    headers.Authorization = pb.authStore.token;
  }

  const method = (options.method || 'GET').toUpperCase();
  if (options.json !== false && (options.body || (method !== 'GET' && method !== 'HEAD'))) {
    headers['Content-Type'] = 'application/json';
  }

  const res = await fetch(path, {
    method: options.method || 'GET',
    headers,
    body: options.body,
    cache: options.cache || 'default'
  });

  const data = await res.json().catch(() => ({}));
  if (!res.ok) {
    const err = new Error(data.message || 'Request failed.');
    err.status = res.status;
    throw err;
  }

  return data;
}
