const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');

(async () => {
  const frontend = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-session-identity-'));
  fs.cpSync(path.join(frontend, 'staff'), path.join(temporary, 'staff'), { recursive: true });
  fs.cpSync(path.join(frontend, 'shared'), path.join(temporary, 'shared'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');
  try {
    const requests = [];
    let identity = '1';
    let holdOldProbe = false;
    let releaseOldProbe;
    global.fetch = async (url, options = {}) => {
      requests.push({ url, method: options.method });
      if (url === '/api/asap/staff/session') {
        const probeIdentity = identity;
        const reply = { ok: true, status: 200, json: async () => ({ authenticated: true, accessAllowed: true,
          antiforgeryToken: `token-${probeIdentity}`, staff: {
            id: probeIdentity, tenantId: '11111111-1111-1111-1111-111111111111',
            authenticationEmail: `${probeIdentity}@example.org`, role: 'staff', organizationId: 2
          } }) };
        if (holdOldProbe) {
          holdOldProbe = false;
          return new Promise(resolve => { releaseOldProbe = () => resolve(reply); });
        }
        return reply;
      }
      if (url === '/api/asap/staff/test-mutation') {
        return { ok: true, status: 200, json: async () => ({ committed: true }) };
      }
      throw new Error(`Unexpected request ${url}`);
    };
    const http = await import(pathToFileURL(path.join(temporary, 'staff/js/http.js')).href);
    let invalidations = 0;
    http.onSessionInvalid(() => { invalidations += 1; });
    await http.loadStaffSession();
    holdOldProbe = true;
    const oldProbe = assert.rejects(http.authorizedJson('/api/asap/staff/test-read'),
      error => error.status === 401 && error.response?.code === 'staff_session_changed');
    assert.equal(typeof releaseOldProbe, 'function');
    identity = '2';
    await assert.rejects(http.authorizedJson('/api/asap/staff/test-mutation', {
      method: 'POST', body: { version: 'old-account' }
    }), error => error.status === 401 && error.response?.code === 'staff_session_changed');
    releaseOldProbe();
    await oldProbe;
    await assert.rejects(http.authorizedJson('/api/asap/staff/test-mutation', {
      method: 'POST', body: { version: 'still-old-account' }
    }), error => error.status === 401 && error.response?.code === 'staff_session_changed');
    assert.equal(invalidations, 1);
    assert.equal(requests.filter(item => item.url === '/api/asap/staff/test-mutation').length, 0,
      'the old account must be invalidated before any mutation is sent');
    console.log('Staff mutation preflight rejects a replaced account before submission');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
