'use strict';
const { test, before, after } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const https = require('node:https');
const { execFile } = require('node:child_process');
const { promisify } = require('node:util');
const { forward } = require('../../dev/review/docker/localhost-forwarder.cjs');
const { openReviewerBrowser, verifyStaffSession } = require('./helpers/authentication.cjs');
const execute = promisify(execFile);
let directory, tls;
before(async () => {
  directory = await fs.mkdtemp(path.join(os.tmpdir(), 'asap-tls-probe-'));
  const output = await execute('dotnet', ['run', '--project', path.join(__dirname, 'tls-probe/TlsProbe.csproj'),
    '--configuration', 'Release', '--', directory], { timeout: 90000, windowsHide: true });
  assert.match(output.stdout, /idempotence, chain trust and partial-state rejection passed/);
  tls = { pfx: await fs.readFile(path.join(directory, 'private/localhost.pfx')),
    passphrase: await fs.readFile(path.join(directory, 'private/password.txt'), 'utf8'),
    ca: await fs.readFile(path.join(directory, 'public/root.crt')) };
});
after(async () => { if (directory) await fs.rm(directory, { recursive: true, force: true }); });
function request(port, address, overrides = {}) {
  return new Promise((resolve, reject) => {
    const req = https.request({ host: address, port, servername: 'localhost', ca: tls.ca,
      method: 'POST', path: '/signin-oidc', headers: { Host: 'localhost:8080', 'Content-Type': 'application/x-www-form-urlencoded' },
      ...overrides }, response => {
      let body = ''; response.on('data', c => { body += c; });
      response.on('end', () => resolve({ status: response.statusCode, body }));
    });
    req.on('error', reject);
    req.end('id_token=synthetic-transport-probe&state=synthetic');
  });
}
test('real review TLS material validates localhost and rejects a mismatched hostname', async t => {
  const server = https.createServer(tls, (_req, res) => res.end('trusted'));
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => { server.closeAllConnections(); server.close(resolve); }));
  assert.equal((await request(server.address().port, '127.0.0.1')).body, 'trusted');
  await assert.rejects(request(server.address().port, '127.0.0.1', { servername: 'unexpected.example.org' }), /Hostname\/IP does not match/);
  await assert.rejects(request(server.address().port, '127.0.0.1', { ca: undefined }), /certificate/);
});
test('localhost forwarder preserves HTTPS, Host and the complete form POST on IPv4 and IPv6', async t => {
  const server = https.createServer(tls, (req, res) => {
    let body = ''; req.on('data', c => { body += c; });
    req.on('end', () => res.end(JSON.stringify({ method: req.method, path: req.url, host: req.headers.host, body })));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const listeners = forward(0, { host: '127.0.0.1', port: server.address().port });
  await Promise.all(listeners.map(listener => new Promise(resolve => listener.once('listening', resolve))));
  t.after(async () => {
    server.closeAllConnections();
    await Promise.all([...listeners, server].map(s => new Promise(resolve => s.close(resolve))));
  });
  for (const listener of listeners) {
    const response = await request(listener.address().port, listener.address().address);
    assert.deepEqual(JSON.parse(response.body), { method: 'POST', path: '/signin-oidc', host: 'localhost:8080',
      body: 'id_token=synthetic-transport-probe&state=synthetic' });
  }
});
test('manual Windows trust helper reads public-only material and honors declined consent', { skip: process.platform !== 'win32' }, async () => {
  const helper = path.resolve(__dirname, '../../scripts/review/Initialize-AsapReviewTls.ps1');
  const script = path.join(directory, 'decline-trust.ps1');
  const quote = value => "'" + value.replace(/'/g, "''") + "'";
  await fs.writeFile(script, 'function Read-Host { return "n" }\n& ' + quote(helper) +
    ' -Workspace ' + quote(directory) + ' -PublicCertificatePath ' + quote(path.join(directory, 'public/root.crt')) + '\n');
  const output = await execute('pwsh', ['-NoProfile', '-File', script], { timeout: 15000, windowsHide: true });
  assert.match(output.stdout, /trust was declined/);
});

test('trusted API bootstrap shares the browser cookie jar without exposing credentials to pages', async t => {
  // Browser transport plumbing only; real cookie issuance and SQL eligibility
  // are covered by the .NET integration journey, not this synthetic HTTP server.
  const credential = require('node:crypto').randomBytes(32).toString('hex');
  const originalRead = fs.readFile;
  t.mock.method(fs, 'readFile', (file, ...args) => file === '/review/auth/credential' ?
    Promise.resolve(credential) : originalRead(file, ...args));
  const requests = [];
  const session = { authenticated: true, accessAllowed: true, staff: {
    id: '17', role: 'super_admin', organizationId: 1, tenantId: 'synthetic-tenant', authenticationEmail: 'REVIEWER@EXAMPLE.ORG'
  } };
  const server = https.createServer(tls, (req, res) => {
    requests.push({ path: req.url, method: req.method, headers: req.headers });
    if (req.url === '/_review/auth/session') {
      assert.equal(req.method, 'POST');
      assert.equal(req.headers['x-asap-review-auth'], credential);
      res.setHeader('Set-Cookie', '__Host-ASAP.Staff=synthetic-transport-cookie; Path=/; Secure; HttpOnly; SameSite=Lax');
      res.writeHead(204); res.end();
    } else if (req.url === '/api/asap/staff/session') {
      res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify(session));
    } else if (req.url.startsWith('/api/asap/staff/title-requests')) {
      res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify({ scope: '2' }));
    } else { res.setHeader('Content-Type', 'text/html'); res.end('<!doctype html><title>Transport only</title><main id="workspace">Cookie storage probe</main>'); }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const { chromium } = require('playwright');
  // Trust validation is separately tested above; avoid changing OS trust for
  // this ephemeral synthetic browser transport server. Run the actual Local
  // orchestration, changing only launch options for this headless TLS fixture.
  let profile, reviewer;
  const launch = chromium.launchPersistentContext.bind(chromium);
  t.mock.method(chromium, 'launchPersistentContext', (directory, options) => {
    profile = directory;
    return launch(directory, { ...options, headless: true, ignoreHTTPSErrors: true });
  });
  t.after(async () => {
    await reviewer?.close().catch(() => {}); server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
  });
  const origin = 'https://localhost:' + server.address().port;
  reviewer = await openReviewerBrowser(origin, 1000, undefined, 'local');
  const { context, page } = reviewer;
  assert.equal(reviewer.mode, 'local');
  assert.equal(reviewer.oidcCallback, undefined);
  assert.equal(await page.evaluate(() => document.cookie), '', 'HttpOnly cookie is not page JavaScript state');
  assert.equal(await page.evaluate(async () => {
    try { await fetch('/api/asap/staff/profile', { method: 'POST' }); return false; }
    catch { return true; }
  }), true, 'Browser writes are blocked before the scenario mutation guard takes over');
  assert.equal(requests.some(request => request.path === '/api/asap/staff/profile'), false);
  const initial = await verifyStaffSession(context, origin);
  assert.equal(initial.staff.id, '17');
  session.staff.id = '18';
  // Snapshot the initial identity, as real responses are separate objects.
  await assert.rejects(verifyStaffSession(context, origin, { ...initial, staff: { ...initial.staff } }), /identity or authorization changed/);
  assert.equal(requests.filter(request => request.path === '/_review/auth/session').length, 1);
  for (const request of requests.filter(request => request.path !== '/_review/auth/session')) {
    assert.equal(request.headers['x-asap-review-auth'], undefined);
    assert.equal(Object.keys(request.headers).some(name => name.startsWith('x-asap-test-')), false);
    assert.match(request.headers.cookie || '', /__Host-ASAP.Staff=synthetic-transport-cookie/);
    assert.doesNotMatch(JSON.stringify(request), new RegExp(credential));
  }
  const close = context.close.bind(context);
  t.mock.method(context, 'close', async () => { await close(); throw new Error('Synthetic browser close failure'); });
  await assert.rejects(reviewer.close(), /Synthetic browser close failure/);
  await assert.rejects(fs.stat(profile), { code: 'ENOENT' });
});
