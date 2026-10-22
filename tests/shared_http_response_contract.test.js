const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

const frontend = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend');
const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-http-response-contract-'));
const dom = new JSDOM('', { url: 'https://localhost/' });

function response(status, body, parseError = null) {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: status >= 400 ? 'Request failed' : 'OK',
    json: async () => {
      if (parseError) throw parseError;
      return body;
    }
  };
}

(async () => {
  try {
    fs.mkdirSync(path.join(temporary, 'shared'), { recursive: true });
    fs.copyFileSync(path.join(frontend, 'shared', 'http.js'), path.join(temporary, 'shared', 'http.js'));
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
    global.window = dom.window;
    global.document = dom.window.document;
    global.FormData = dom.window.FormData;
    const { HttpError, requestJson } = await import(pathToFileURL(path.join(temporary, 'shared', 'http.js')));

    global.fetch = async () => response(204, null);
    assert.equal(await requestJson('/logout', { method: 'POST', allowNoContent: true }), null,
      'declared 204 endpoints remain valid');
    await assert.rejects(
      requestJson('/required', { method: 'POST', body: {} }),
      error => error instanceof HttpError && error.status === 204 &&
        error.response?.code === 'empty_response' && error.outcomeUnknown,
      'an undeclared empty mutation response must be rejected as uncertain');

    global.fetch = async () => response(200, null, new SyntaxError('truncated JSON'));
    await assert.rejects(
      requestJson('/read'),
      error => error instanceof HttpError && error.status === 200 &&
        error.response?.code === 'invalid_response_json' && !error.outcomeUnknown,
      'malformed successful JSON must not become an empty object');

    global.fetch = async () => response(201, { ok: true });
    await assert.rejects(
      requestJson('/created', { method: 'POST', requireObjectResponse: true,
        validateResponse: body => { if (typeof body.id !== 'string') throw new Error('missing string id'); } }),
      error => error instanceof HttpError && error.status === 201 &&
        error.response?.code === 'invalid_response_shape' && error.outcomeUnknown,
      'a malformed committed mutation result must be rejected and marked uncertain');

    global.fetch = async () => response(200, []);
    await assert.rejects(
      requestJson('/object', { requireObjectResponse: true }),
      error => error.response?.code === 'invalid_response_shape',
      'required object contracts must reject arrays');

    global.fetch = async () => response(500, { message: 'server failed after dispatch' });
    await assert.rejects(
      requestJson('/mutation', { method: 'POST', body: {} }),
      error => error.status === 500 && error.outcomeUnknown,
      'server failures after a mutation may leave a committed result');

    global.fetch = async () => response(400, null, new SyntaxError('invalid error JSON'));
    await assert.rejects(
      requestJson('/rejected', { method: 'POST', body: {} }),
      error => error.status === 400 && error.response === null && !error.outcomeUnknown,
      'a known client rejection stays distinguishable when its error body is malformed');

    const aborted = Object.assign(new Error('aborted'), { name: 'AbortError' });
    global.fetch = async () => { throw aborted; };
    await assert.rejects(
      requestJson('/aborted-mutation', { method: 'POST', body: {} }),
      error => error.name === 'AbortError' && error.outcomeUnknown,
      'aborted mutation requests keep cancellation visible and report the uncertain outcome');

    const bodyAborted = Object.assign(new Error('response body read aborted'), { name: 'AbortError' });
    global.fetch = async () => response(201, null, bodyAborted);
    await assert.rejects(
      requestJson('/aborted-response-body', { method: 'POST', body: {} }),
      error => error === bodyAborted && error.outcomeUnknown,
      'cancellation while reading a successful mutation body must remain an AbortError');
  } finally {
    dom.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
  console.log('Shared HTTP JSON response contracts and uncertain mutation outcomes passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
