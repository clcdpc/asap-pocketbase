const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');
const { JSDOM } = require('jsdom');
const { patronSession, patronUiText } = require('./helpers/patron-session-fixture');

function response(status, body) {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: status === 401 ? 'Unauthorized' : 'OK',
    json: async () => body
  };
}

function session(barcode, token, overrides = {}) {
  return patronSession(barcode, token, overrides);
}

function aLibraryUiText() {
  const configuration = patronUiText('A', {
    customField: { key: 'a_subject', label: 'A Subject', ruleLabel: null, mode: 'required' }
  });
  configuration.formatRules.library_a_format = {
    ...configuration.formatRules.book,
    fields: { ...configuration.formatRules.book.fields },
    customFields: { ...configuration.formatRules.book.customFields }
  };
  configuration.formatLabels.library_a_format = 'A Special Format';
  configuration.availableFormats = ['library_a_format', 'ebook'];
  return configuration;
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
    let loginResponseOverride = null;
    let suggestionRequests = 0;
    global.fetch = async (url, options = {}) => {
      const requestUrl = String(url);
      if (requestUrl.endsWith('/api/asap/patron/session')) {
        return new Promise(resolve => {
          pendingRestore = resolve;
        });
      }
      if (requestUrl.endsWith('/api/asap/patron/login')) {
        const data = JSON.parse(options.body);
        return response(200, loginResponseOverride || session(data.username, 'token-' + data.username));
      }
      if (requestUrl.endsWith('/api/asap/patron/logout')) return response(204, null);
      if (requestUrl.endsWith('/api/asap/patron/suggestions')) {
        suggestionRequests++;
        return response(201, { id: '1', successTitle: 'Saved', successMessage: 'Saved' });
      }
      throw new Error('Unexpected request: ' + requestUrl);
    };
    const initialFetch = global.fetch;

    const auth = await import(pathToFileURL(path.join(temporary, 'patron', 'js', 'auth.js')).href);
    const state = await import(pathToFileURL(path.join(temporary, 'patron', 'js', 'state.js')).href);
    const config = await import(pathToFileURL(path.join(temporary, 'patron', 'js', 'config.js')).href);
    const submit = await import(pathToFileURL(path.join(temporary, 'patron', 'js', 'submit.js')).href);
    const barcode = document.getElementById('barcode');
    const pin = document.getElementById('pin');

    let queuedLoginPayload;
    let queuedRestorePayload;
    global.fetch = async (url, options = {}) => {
      const requestUrl = String(url);
      if (requestUrl.endsWith('/api/asap/patron/login')) return response(200, queuedLoginPayload);
      if (requestUrl.endsWith('/api/asap/patron/session')) return response(200, queuedRestorePayload);
      if (requestUrl.endsWith('/api/asap/patron/logout')) return response(204, null);
      if (requestUrl.endsWith('/api/asap/patron/suggestions')) {
        suggestionRequests++;
        return response(201, { id: '1', successTitle: 'Saved', successMessage: 'Saved' });
      }
      throw new Error('Unexpected request: ' + requestUrl);
    };

    const invalidUiTextCases = [
      { name: 'missing ui_text', create: () => undefined },
      { name: 'null ui_text', create: () => null },
      { name: 'array ui_text', create: () => [] },
      { name: 'partial ui_text', create: () => ({ pageTitle: 'B Material Suggestion' }) },
      { name: 'object availableFormats', create: () => patronUiText('B', { availableFormats: {} }) },
      { name: 'array formatRules', create: () => patronUiText('B', { formatRules: [] }) }
    ];
    const aUiText = aLibraryUiText();
    function uiSnapshot() {
      return {
        pageTitle: document.title,
        heading: document.getElementById('main-title').textContent,
        displayedBarcode: document.getElementById('display-barcode').textContent,
        formatOptions: Array.from(document.getElementById('format').options)
          .map(option => [option.value, option.textContent]),
        publicationOptions: Array.from(document.getElementById('publication').options)
          .map(option => option.value),
        customFields: Array.from(document.querySelectorAll('.custom-field-row'))
          .map(row => [row.dataset.customFieldKey, row.querySelector('.custom-field-input').required])
      };
    }

    for (const channel of ['login', 'restore']) {
      for (const invalidUiText of invalidUiTextCases) {
        state.setAuthToken('');
        queuedLoginPayload = session('A20000000000921', 'token-A20000000000921', { ui_text: aUiText });
        barcode.value = 'A20000000000921';
        pin.value = '1234';
        await auth.handleLoginSubmit({ preventDefault() {} });
        assert.strictEqual(state.authToken, 'token-A20000000000921', `${channel}: A session starts with a complete producer-shaped snapshot`);
        const expectedUi = uiSnapshot();

        const invalidPayload = session('B20000000000922', channel === 'restore' ? null : 'token-B20000000000922', {
          effectiveLibraryOrgId: 3,
          ui_text: invalidUiText.create()
        });
        if (invalidUiText.name === 'missing ui_text') delete invalidPayload.ui_text;

        if (channel === 'login') {
          await auth.logout();
          queuedLoginPayload = invalidPayload;
          barcode.value = 'B20000000000922';
          pin.value = '1234';
          await auth.handleLoginSubmit({ preventDefault() {} });
        } else {
          queuedRestorePayload = invalidPayload;
          await auth.restoreSession();
        }

        assert.strictEqual(state.authToken, '', `${channel} rejects ${invalidUiText.name} before establishing B`);
        assert.strictEqual(sessionStorage.getItem('asap_patron_token'), null,
          `${channel} ${invalidUiText.name} cannot persist B's token`);
        assert.strictEqual(document.getElementById('step-form').classList.contains('hidden'), true,
          `${channel} ${invalidUiText.name} cannot open B's suggestion form`);
        assert.deepStrictEqual(uiSnapshot(), expectedUi,
          `${channel} ${invalidUiText.name} cannot replace A's identity or configuration DOM`);
      }
    }
    global.fetch = initialFetch;

    // A complete no-format login must clear the preceding A configuration.
    state.setAuthToken('');
    loginResponseOverride = session('F20000000000900', 'token-F20000000000900', {
      ui_text: patronUiText('Fresh', { availableFormats: [] })
    });
    barcode.value = 'F20000000000900';
    pin.value = '1234';
    await auth.handleLoginSubmit({ preventDefault() {} });
    assert.strictEqual(config.uiConfig.pageTitle, 'Fresh Material Suggestion');
    assert.deepStrictEqual(Array.from(document.getElementById('format').options), [],
      'a complete no-format session snapshot clears the previous format options');
    assert.strictEqual(document.getElementById('format').checkValidity(), false,
      'an empty required format selector cannot validate a suggestion');
    const freshSuggestionCount = suggestionRequests;
    await submit.handleSuggestionSubmit({ preventDefault() {} });
    assert.strictEqual(suggestionRequests, freshSuggestionCount,
      'an empty enabled-format list cannot dispatch a suggestion');
    assert.match(document.getElementById('submit-error').textContent, /format.*available/i,
      'an empty enabled-format list explains why submission is unavailable');
    await auth.logout();

    // An empty list from library B must replace library A's options and custom fields.
    loginResponseOverride = session('A20000000000911', 'token-A20000000000911', {
      ui_text: aLibraryUiText()
    });
    barcode.value = 'A20000000000911';
    pin.value = '1234';
    await auth.handleLoginSubmit({ preventDefault() {} });
    assert.deepStrictEqual(Array.from(document.getElementById('format').options).map(option => option.value), ['library_a_format', 'ebook']);
    assert.strictEqual(document.getElementById('format').options[0].textContent, 'A Special Format');
    assert.deepStrictEqual(Array.from(document.querySelectorAll('.custom-field-row')).map(row => row.dataset.customFieldKey), ['a_subject']);
    assert.deepStrictEqual(Array.from(document.getElementById('publication').options).map(option => option.value), ['A publication']);
    await auth.logout();

    loginResponseOverride = session('B20000000000912', 'token-B20000000000912', {
      effectiveLibraryOrgId: 3,
      ui_text: patronUiText('B', { availableFormats: [] })
    });
    barcode.value = 'B20000000000912';
    pin.value = '1234';
    await auth.handleLoginSubmit({ preventDefault() {} });
    assert.strictEqual(config.uiConfig.pageTitle, 'B Material Suggestion');
    assert.deepStrictEqual(Array.from(document.getElementById('format').options), [],
      'library B empty formats replace library A options');
    assert.deepStrictEqual(Array.from(document.querySelectorAll('.custom-field-row')), [],
      'library B empty custom fields remove library A fields');
    assert.deepStrictEqual(Array.from(document.getElementById('publication').options).map(option => option.value), ['B publication']);
    const libraryBSuggestionCount = suggestionRequests;
    await submit.handleSuggestionSubmit({ preventDefault() {} });
    assert.strictEqual(suggestionRequests, libraryBSuggestionCount,
      'library B with no enabled formats cannot dispatch a suggestion');
    await auth.logout();
    loginResponseOverride = null;
    state.setAuthToken('token-A');

    const restoreA = auth.restoreSession();
    await Promise.resolve();
    barcode.value = 'B20000000000902';
    pin.value = '1234';
    await auth.handleLoginSubmit({ preventDefault() {} });
    assert.strictEqual(document.getElementById('display-barcode').textContent, 'B20000000000902');
    assert.strictEqual(sessionStorage.getItem('asap_patron_token'), 'token-B20000000000902');

    pendingRestore(response(200, session('A20000000000901', null)));
    await restoreA;
    assert.strictEqual(document.getElementById('display-barcode').textContent, 'B20000000000902', 'stale valid restore must not replace the newer login UI');
    assert.strictEqual(sessionStorage.getItem('asap_patron_token'), 'token-B20000000000902', 'stale valid restore must not replace the newer token');

    const invalidRestoreA = auth.restoreSession();
    await Promise.resolve();
    barcode.value = 'C20000000000903';
    pin.value = '1234';
    await auth.handleLoginSubmit({ preventDefault() {} });
    pendingRestore(response(200, session('A20000000000901', null, {
      pickupBranches: [{ id: '101', label: 'Main Library' }]
    })));
    await invalidRestoreA;
    assert.strictEqual(document.getElementById('display-barcode').textContent, 'C20000000000903',
      'stale invalid restore must not replace the newer login UI');
    assert.strictEqual(sessionStorage.getItem('asap_patron_token'), 'token-C20000000000903',
      'stale invalid restore must not replace the newer token');

    const restoreB = auth.restoreSession();
    await Promise.resolve();
    barcode.value = 'D20000000000904';
    pin.value = '1234';
    await auth.handleLoginSubmit({ preventDefault() {} });
    pendingRestore(response(401, { message: 'expired' }));
    await restoreB;

    assert.strictEqual(document.getElementById('display-barcode').textContent, 'D20000000000904', 'stale failed restore must not replace the newer login UI');
    assert.strictEqual(sessionStorage.getItem('asap_patron_token'), 'token-D20000000000904', 'stale failed restore must not clear the newer token');
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

    state.setAuthToken('token-invalid-restore');
    global.fetch = async url => {
      if (String(url).endsWith('/api/asap/patron/session')) return response(200, {});
      if (String(url).endsWith('/api/asap/patron/logout')) return response(204, null);
      if (String(url).endsWith('/api/asap/patron/login')) {
        return response(200, session('E20000000000905', 'token-E20000000000905', {
          effectiveLibraryOrgId: '2'
        }));
      }
      throw new Error('Unexpected request: ' + url);
    };
    const priorInvalidRestoreBarcode = document.getElementById('display-barcode').textContent;
    await auth.restoreSession();
    assert.strictEqual(sessionStorage.getItem('asap_patron_token'), null,
      'an empty restore envelope cannot establish a patron session');
    assert.strictEqual(state.authToken, '', 'an invalid restore clears the active token');
    assert.strictEqual(document.getElementById('display-barcode').textContent, priorInvalidRestoreBarcode,
      'an invalid restore cannot replace the prior, now-hidden identity');
    assert.strictEqual(document.getElementById('step-form').classList.contains('hidden'), true,
      'an invalid restore remains at login');
    assert.strictEqual(document.getElementById('login-error').classList.contains('hidden'), false);

    barcode.value = 'E20000000000905';
    pin.value = '1234';
    const priorInvalidLoginBarcode = document.getElementById('display-barcode').textContent;
    await auth.handleLoginSubmit({ preventDefault() {} });
    assert.strictEqual(sessionStorage.getItem('asap_patron_token'), null,
      'a login with a string organization id is not accepted as a valid session');
    assert.strictEqual(state.authToken, '', 'a malformed login cannot establish the active token');
    assert.strictEqual(document.getElementById('display-barcode').textContent, priorInvalidLoginBarcode,
      'a malformed successful login cannot replace the prior, hidden identity');
    assert.strictEqual(document.getElementById('step-form').classList.contains('hidden'), true,
      'a malformed successful login remains at login');
    assert.strictEqual(document.getElementById('login-error').classList.contains('hidden'), false);

    let restorePayload;
    global.fetch = async url => {
      if (String(url).endsWith('/api/asap/patron/session')) return response(200, restorePayload);
      throw new Error('Unexpected request: ' + url);
    };
    for (const [label, selectedPickupBranchId, omitSelected, omitToken] of [
      ['omitted selected branch and token', undefined, true, true],
      ['null selected branch', null, false, false]
    ]) {
      state.setAuthToken(`token-valid-${label}`);
      restorePayload = session(`F20000000000${omitSelected ? '906' : '907'}`, null, {
        pickupBranches: [], selectedPickupBranchId
      });
      if (omitSelected) delete restorePayload.selectedPickupBranchId;
      if (omitToken) delete restorePayload.token;
      await auth.restoreSession();
      assert.strictEqual(document.getElementById('display-barcode').textContent,
        restorePayload.barcode, `${label}: restore accepts the valid session and empty branch list`);
      assert.strictEqual(document.getElementById('step-form').classList.contains('hidden'), false,
        `${label}: restore can establish the patron view`);
    }

    for (const selectedPickupBranchId of [0, -1]) {
      state.setAuthToken(`token-invalid-selected-${selectedPickupBranchId}`);
      restorePayload = session('G20000000000908', null, {
        pickupBranches: [], selectedPickupBranchId
      });
      const priorBarcode = document.getElementById('display-barcode').textContent;
      await auth.restoreSession();
      assert.strictEqual(sessionStorage.getItem('asap_patron_token'), null,
        `selected pickup branch ${selectedPickupBranchId} is not a valid restored session`);
      assert.strictEqual(state.authToken, '',
        `selected pickup branch ${selectedPickupBranchId} cannot establish the active token`);
      assert.strictEqual(document.getElementById('display-barcode').textContent, priorBarcode,
        `selected pickup branch ${selectedPickupBranchId} cannot replace the prior, hidden identity`);
      assert.strictEqual(document.getElementById('step-form').classList.contains('hidden'), true);
      assert.strictEqual(document.getElementById('login-error').classList.contains('hidden'), false);
    }

    console.log('Patron auth restore race regression checks passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
