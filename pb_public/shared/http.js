export class HttpError extends Error {
  constructor(message, status, response) {
    super(message || 'Request failed.');
    this.name = 'HttpError';
    this.status = status || 0;
    this.response = response || null;
  }
}

export function isAbortError(err) {
  return !!(err && (
    err.name === 'AbortError' ||
    err.code === 'ABORT_ERR' ||
    /abort/i.test(String(err.message || ''))
  ));
}

function shouldSerializeJsonBody(body) {
  if (!body || typeof body !== 'object') return false;
  if (body instanceof FormData) return false;
  if (body instanceof URLSearchParams) return false;
  if (typeof Blob !== 'undefined' && body instanceof Blob) return false;
  if (typeof ArrayBuffer !== 'undefined' && body instanceof ArrayBuffer) return false;
  return !(typeof body === 'string');
}

export async function requestJson(path, options = {}) {
  const headers = { ...(options.headers || {}) };
  const method = String(options.method || 'GET').toUpperCase();
  let body = options.body;

  if (options.json !== false && shouldSerializeJsonBody(body)) {
    headers['Content-Type'] = headers['Content-Type'] || 'application/json';
    body = JSON.stringify(body);
  }

  try {
    const response = await fetch(path, {
      method,
      headers,
      body,
      cache: options.cache || 'default',
      signal: options.signal
    });

    const data = await response.json().catch(() => ({}));
    if (!response.ok) {
      throw new HttpError(data.message || response.statusText || 'Request failed.', response.status, data);
    }

    return data;
  } catch (err) {
    if (isAbortError(err)) {
      throw err;
    }
    if (err instanceof HttpError) {
      throw err;
    }
    throw new HttpError(err && err.message ? err.message : 'Request failed.', err && err.status, err && err.response);
  }
}
