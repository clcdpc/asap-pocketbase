const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');
const { patronSession, patronUiText } = require('./helpers/patron-session-fixture');

function response(status, body) {
  return { ok: status >= 200 && status < 300, status, statusText: 'Test response', json: async () => body };
}

async function exercise(mode) {
  const frontend = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-patron-storage-'));
  const dom = new JSDOM(fs.readFileSync(path.join(frontend, 'patron', 'index.html'), 'utf8'), {
    url: 'https://localhost/patron/?libraryOrgId=3'
  });
  try {
    fs.cpSync(path.join(frontend, 'patron'), path.join(temporary, 'patron'), { recursive: true });
    fs.cpSync(path.join(frontend, 'shared'), path.join(temporary, 'shared'), { recursive: true });
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
    global.window = dom.window;
    global.document = dom.window.document;
    global.FormData = dom.window.FormData;
    global.Option = dom.window.Option;
    global.Event = dom.window.Event;
    let deny = mode;
    const persisted = { sessionStorage: new Map([['asap_patron_token', 'token-A']]),
      localStorage: new Map([['asap_patron_library_org_id', '2']]) };
    for (const name of Object.keys(persisted)) {
      const storage = {
        getItem(key) {
          if (deny === 'read') throw new Error('Read denied');
          return persisted[name].get(key) ?? null;
        },
        setItem(key, value) {
          if (deny) throw new Error('Write denied');
          persisted[name].set(key, value);
        },
        removeItem(key) {
          if (deny) throw new Error('Removal denied');
          persisted[name].delete(key);
        }
      };
      Object.defineProperty(global, name, { configurable: true, get() {
        if (deny === 'getter') throw new Error('Storage access denied');
        return storage;
      } });
    }
    const requests = [];
    let restoreStatus = 200;
    global.fetch = async (url, options = {}) => {
      const request = { url: new URL(url), ...options };
      requests.push(request);
      if (request.url.pathname === '/api/asap/config') return response(200, patronUiText('Initial'));
      if (request.url.pathname.endsWith('/session')) {
        return response(restoreStatus, restoreStatus === 200 ? patronSession('A', null) : { message: 'Expired' });
      }
      if (request.url.pathname.endsWith('/login')) {
        const data = JSON.parse(options.body);
        return response(200, patronSession(data.username, 'token-' + data.username, { effectiveLibraryOrgId: 4 }));
      }
      if (request.url.pathname.endsWith('/logout')) return response(204, null);
      throw new Error('Unexpected request: ' + url);
    };
    const load = name => import(pathToFileURL(path.join(temporary, 'patron', 'js', name + '.js')));
    const bootstrap = await load('bootstrap');
    const auth = await load('auth');
    const state = await load('state');
    const config = await load('config');
    const api = await load('api');
    assert.equal(state.authToken, mode ? '' : 'token-A', mode + ': startup tolerates denied storage');
    dom.reconfigure({ url: 'https://localhost/patron/' });
    assert.equal(auth.getPatronExperienceLibraryOrgId(), mode ? '' : '2', 'preference reads tolerate storage denial');
    dom.reconfigure({ url: 'https://localhost/patron/?libraryOrgId=3' });
    await bootstrap.initPatronApp();
    assert.equal(requests[0].url.searchParams.get('libraryOrgId'), '3', 'URL scope survives failed preference persistence');
    if (!mode) {
      assert.equal(persisted.localStorage.get('asap_patron_library_org_id'), '3', 'working preferences persist');
      deny = 'write/remove';
    }

    async function login(barcode) {
      document.getElementById('barcode').value = barcode;
      document.getElementById('pin').value = '1234';
      await auth.handleLoginSubmit({ preventDefault() {} });
      assert.equal(state.authToken, 'token-' + barcode);
      assert.equal(document.getElementById('display-barcode').textContent, barcode);
      assert.equal(document.title, barcode + ' Material Suggestion');
      assert.equal(document.getElementById('step-form').classList.contains('hidden'), false);
      assert.equal(document.getElementById('login-error').classList.contains('hidden'), true);
    }
    await login('B');
    assert.equal(persisted.sessionStorage.get('asap_patron_token'), 'token-A', 'failed write leaves the old persistent token');
    dom.reconfigure({ url: 'https://localhost/patron/' });
    assert.equal(auth.getPatronExperienceLibraryOrgId(), '4', 'accepted library wins over older persistence');
    assert.equal(new URL(config.getConfigUrl(), window.location.origin).searchParams.get('libraryOrgId'), '4');
    document.cookie = 'asap_patron_library_org_id=5';
    assert.equal(auth.getPatronExperienceLibraryOrgId(), '5', 'auth retains cookie precedence');
    assert.equal(new URL(config.getConfigUrl(), window.location.origin).searchParams.get('libraryOrgId'), '4',
      'initial configuration retains its preference-only fallback');
    await api.restorePatronSession();
    assert.equal(requests.at(-1).headers.Authorization, 'Bearer token-B');
    state.setSubmitOutcomeUnknown(true);
    state.setSubmitInProgress(true);
    document.getElementById('title').value = 'Unsaved draft';
    await auth.logout();
    assert.equal(requests.at(-1).headers.Authorization, 'Bearer token-B', 'logout revokes the latest accepted session');
    function assertCleared() {
      assert.equal(state.authToken, '');
      assert.equal(state.submitOutcomeUnknown, false);
      assert.equal(state.submitInProgress, false);
      assert.equal(document.getElementById('step-login').classList.contains('hidden'), false);
      assert.equal(document.getElementById('title').value, '');
      assert.equal(document.getElementById('barcode').value, '');
      assert.equal(document.getElementById('login-btn').disabled, false);
      assert.equal(document.getElementById('submit-btn').textContent, 'Submit');
    }
    assertCleared();
    assert.equal(persisted.sessionStorage.get('asap_patron_token'), 'token-A', 'denied removal does not claim persistent clearing');
    await login('C');
    restoreStatus = 401;
    await auth.restoreSession();
    assertCleared();
    await login('D');
    auth.handleSessionExpired(new api.SessionExpiredError('Expired', 'token-D'));
    assertCleared();
    await login('E');
    auth.handleSessionExpired(new api.SessionExpiredError('Old session expired', 'token-D'));
    assert.equal(state.authToken, 'token-E', 'an old request cannot expire a newer session');
  } finally {
    delete global.localStorage;
    delete global.sessionStorage;
    dom.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
}

(async () => {
  for (const mode of ['getter', 'read', '']) await exercise(mode);
  console.log('Patron storage failure flows passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
