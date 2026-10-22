export class HttpError extends Error {
  constructor(message, status, response, options = {}) {
    super(message || 'Request failed.');
    this.name = 'HttpError';
    this.status = status || 0;
    this.response = response || null;
    this.outcomeUnknown = Boolean(options.outcomeUnknown);
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

    if (response.status === 204 || method === 'HEAD') {
      if (response.ok && options.allowNoContent === true) return null;
      if (response.ok) {
        throw new HttpError('The server returned an empty response where JSON was required.', response.status,
          { code: 'empty_response' }, { outcomeUnknown: isMutationMethod(method) });
      }
      if (!response.ok) {
        throw new HttpError(response.statusText || 'Request failed.', response.status, null,
          { outcomeUnknown: isMutationMethod(method) });
      }
      return null;
    }

    let data;
    try {
      data = await response.json();
    } catch (error) {
      if (isAbortError(error)) throw error;
      if (response.ok) {
        throw new HttpError('The server returned an invalid JSON response.', response.status,
          { code: 'invalid_response_json' }, { outcomeUnknown: isMutationMethod(method) });
      }
      data = null;
    }

    if (!response.ok) {
      throw new HttpError(data?.message || response.statusText || 'Request failed.', response.status, data,
        { outcomeUnknown: isMutationMethod(method) && (response.status === 408 || response.status >= 500) });
    }

    if (options.requireObjectResponse &&
        (!data || typeof data !== 'object' || Array.isArray(data))) {
      throw new HttpError('The server returned an invalid response body.', response.status,
        { code: 'invalid_response_shape' }, { outcomeUnknown: isMutationMethod(method) });
    }
    if (typeof options.validateResponse === 'function') {
      try {
        options.validateResponse(data, response);
      } catch (error) {
        if (isAbortError(error)) throw error;
        throw new HttpError('The server returned an invalid response body.', response.status,
          { code: 'invalid_response_shape' }, { outcomeUnknown: isMutationMethod(method) });
      }
    }

    return data;
  } catch (err) {
    if (isAbortError(err)) {
      if (isMutationMethod(method)) err.outcomeUnknown = true;
      throw err;
    }
    if (err instanceof HttpError) {
      throw err;
    }
    throw new HttpError(err && err.message ? err.message : 'Request failed.', err && err.status,
      err && err.response, { outcomeUnknown: isMutationMethod(method) });
  }
}

function isMutationMethod(method) {
  return !['GET', 'HEAD', 'OPTIONS'].includes(method);
}
