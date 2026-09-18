const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');
const { JSDOM } = require('jsdom');

function response(status, body) {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: status === 401 ? 'Unauthorized' : 'OK',
    json: async () => body
  };
}

function session(barcode, token) {
  return {
    token,
    barcode,
    email: barcode + '@example.org',
    preferredPickupBranchId: 101,
    selectedPickupBranchId: 101,
    pickupBranches: [{ id: 101, label: 'Main Library' }],
    record: { email: barcode + '@example.org', libraryOrgId: 2 },
    effectiveLibraryOrgId: 2
  };
}

(async () => {
  const root = path.join(__dirname, '..');
  const source = path.join(root, 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-patron-auth-race-'));
  try {
    fs.cpSync(path.join(source, 'patron'), path.join(temporary, 'patron'), { recursive: true });
    fs.cpSync(path.join(source, 'shared'), path.join(temporary, 'shared'), { recursive: true });
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');

    const html = fs.readFileSync(path.join(source, 'patron', 'index.html'), 'utf8');
    const dom = new JSDOM(html, { url: 'http://localhost/patron/?libraryOrgId=2' });
    global.window = dom.window;
    global.document = dom.window.document;
    global.localStorage = dom.window.localStorage;
    global.sessionStorage = dom.window.sessionStorage;
    global.FormData = dom.window.FormData;
    global.Option = dom.window.Option;
    global.Event = dom.window.Event;
    sessionStorage.setItem('asap_patron_token', 'token-A');

    let pendingRestore;
    global.fetch = async (url, options = {}) => {
      const requestUrl = String(url);
      if (requestUrl.endsWith('/api/asap/patron/session')) {
        return new Promise(resolve => {
          pendingRestore = resolve;
        });
      }
      if (requestUrl.endsWith('/api/asap/patron/login')) {
        const data = JSON.parse(options.body);
        return response(200, session(data.username, 'token-' + data.username));
      }
      throw new Error('Unexpected request: ' + requestUrl);
    };

    const auth = await import(pathToFileURL(path.join(temporary, 'patron', 'js', 'auth.js')).href);
    const state = await import(pathToFileURL(path.join(temporary, 'patron', 'js', 'state.js')).href);
    const barcode = document.getElementById('barcode');
    const pin = document.getElementById('pin');

    const restoreA = auth.restoreSession();
    await Promise.resolve();
    barcode.value = 'B20000000000902';
    pin.value = '1234';
    await auth.handleLoginSubmit({ preventDefault() {} });
    assert.strictEqual(document.getElementById('display-barcode').textContent, 'B20000000000902');
    assert.strictEqual(sessionStorage.getItem('asap_patron_token'), 'token-B20000000000902');

    pendingRestore(response(200, session('A20000000000901', null)));
    await restoreA;
    assert.strictEqual(document.getElementById('display-barcode').textContent, 'B20000000000902', 'stale successful restore must not replace the newer login UI');
    assert.strictEqual(sessionStorage.getItem('asap_patron_token'), 'token-B20000000000902', 'stale successful restore must not replace the newer token');

    const restoreB = auth.restoreSession();
    await Promise.resolve();
    barcode.value = 'C20000000000903';
    pin.value = '1234';
    await auth.handleLoginSubmit({ preventDefault() {} });
    pendingRestore(response(401, { message: 'expired' }));
    await restoreB;

    assert.strictEqual(document.getElementById('display-barcode').textContent, 'C20000000000903', 'stale failed restore must not replace the newer login UI');
    assert.strictEqual(sessionStorage.getItem('asap_patron_token'), 'token-C20000000000903', 'stale failed restore must not clear the newer token');
    assert.strictEqual(document.getElementById('step-form').classList.contains('hidden'), false, 'stale failed restore must not return the newer session to login');

    state.setAuthToken('token-A');
    const startupOperation = auth.captureAuthOperation();
    let finishLogin;
    let restoreRequestCount = 0;
    global.fetch = async (url, options = {}) => {
      const requestUrl = String(url);
      if (requestUrl.endsWith('/api/asap/patron/session')) {
        restoreRequestCount++;
        return response(200, session('A20000000000901', null));
      }
      if (requestUrl.endsWith('/api/asap/patron/login')) {
        const data = JSON.parse(options.body);
        return new Promise(resolve => {
          finishLogin = () => resolve(response(200, session(data.username, 'token-' + data.username)));
        });
      }
      throw new Error('Unexpected request: ' + requestUrl);
    };

    barcode.value = 'D20000000000904';
    pin.value = '1234';
    const loginD = auth.handleLoginSubmit({ preventDefault() {} });
    await Promise.resolve();
    await auth.restoreSession(startupOperation);
    assert.strictEqual(restoreRequestCount, 0, 'startup restore must not supersede a foreground login begun while configuration loaded');
    finishLogin();
    await loginD;
    assert.strictEqual(document.getElementById('display-barcode').textContent, 'D20000000000904');
    assert.strictEqual(sessionStorage.getItem('asap_patron_token'), 'token-D20000000000904');

    console.log('Patron auth restore race regression checks passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
