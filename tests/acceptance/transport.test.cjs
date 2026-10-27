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
