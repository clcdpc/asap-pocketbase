import { pb } from './state.js';
import { requestJson, isAbortError } from '../../shared/http.js';

export async function authorizedJson(path, options = {}) {
  const headers = { ...(options.headers || {}) };
  if (pb.authStore.token) {
    headers.Authorization = pb.authStore.token;
  }
  return requestJson(path, {
    ...options,
    headers
  });
}

export { isAbortError };
