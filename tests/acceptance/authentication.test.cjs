'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const { EventEmitter } = require('node:events');
const { authenticateLocal, authenticateEntra } = require('./helpers/authentication.cjs');
const origin = 'https://localhost:8080';

test('local bootstrap rejects unavailable/malformed credentials, failed responses and missing or persistent cookies', async t => {
  let credential = 'a'.repeat(64), unavailable = false, status = 204, cookies = [];
  t.mock.method(fs, 'readFile', async () => {
    if (unavailable) throw new Error('secret-error-canary');
    return credential;
  });
  const context = { request: { post: async (url, options) => {
    assert.equal(url, origin + '/_review/auth/session');
    assert.equal(options.maxRedirects, 0);
    assert.deepEqual(Object.keys(options.headers), ['X-ASAP-Review-Auth']);
    assert.ok(options.headers['X-ASAP-Review-Auth'] === credential);
    return { status: () => status };
  } }, cookies: async () => cookies };
  unavailable = true;
  await assert.rejects(authenticateLocal(context, origin), error => /credential is unavailable/.test(error.message) && !/canary/.test(error.message));
  unavailable = false; credential = 'malformed';
  await assert.rejects(authenticateLocal(context, origin), /credential is malformed/);
  credential = 'a'.repeat(64); status = 403;
  await assert.rejects(authenticateLocal(context, origin), /authentication failed/);
  status = 204;
  await assert.rejects(authenticateLocal(context, origin), /normal ephemeral staff cookie/);
  cookies = [{ name: '__Host-ASAP.Staff', secure: true, httpOnly: true, path: '/', sameSite: 'Lax', expires: 1234567890 }];
  await assert.rejects(authenticateLocal(context, origin), /normal ephemeral staff cookie/);
});

// Callback-observation unit tests only; these do not validate an Entra sign-in.
function entraTransport(response) {
  const page = new EventEmitter();
  page.goto = async () => { if (response) page.emit('response', response); };
  page.url = () => origin + '/staff/';
  page.isClosed = () => false;
  const context = { request: { get: async () => ({ ok: () => true,
    json: async () => ({ authenticated: true, accessAllowed: true }) }) } };
  return { context, page };
}
function callback(method, status, url = origin + '/signin-oidc') {
  return { url: () => url, status: () => status, request: () => ({ method: () => method }) };
}
test('Entra still requires a successful local form POST callback even with an authenticated session', async () => {
  for (const response of [null, callback('GET', 302), callback('POST', 200), callback('POST', 302, 'https://other.example.org/signin-oidc')]) {
    const { context, page } = entraTransport(response);
    await assert.rejects(authenticateEntra(context, page, origin, 1000), /real Entra form POST callback/);
    assert.equal(page.listenerCount('response'), 0);
  }
});
test('Entra callback evidence retains only method, path and status, with listeners removed', async () => {
  const { context, page } = entraTransport(callback('POST', 302));
  assert.deepEqual(await authenticateEntra(context, page, origin, 1000), { method: 'POST', path: '/signin-oidc', status: 302 });
  assert.equal(page.listenerCount('response'), 0);
});
