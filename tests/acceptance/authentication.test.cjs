'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const { authenticateLocal } = require('./helpers/authentication.cjs');
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
