const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

const frontend = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend');
const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-patron-submit-race-'));
const dom = new JSDOM(fs.readFileSync(path.join(frontend, 'patron', 'index.html'), 'utf8'), {
  url: 'https://localhost/patron/?libraryOrgId=2',
  pretendToBeVisual: true
});

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

function loginBody(barcode, token) {
  return {
    token,
    barcode,
    email: `${barcode}@example.org`,
    record: { email: `${barcode}@example.org`, libraryOrgId: 2 },
    effectiveLibraryOrgId: 2,
    preferredPickupBranchId: 101,
    selectedPickupBranchId: 101,
    pickupBranches: [{ id: 101, label: 'Main Library' }]
  };
}

async function until(predicate, message) {
  const deadline = Date.now() + 3000;
  while (!predicate() && Date.now() < deadline) {
    await new Promise(resolve => setTimeout(resolve, 5));
  }
  assert.ok(predicate(), message);
}

(async () => {
  try {
    fs.cpSync(path.join(frontend, 'patron'), path.join(temporary, 'patron'), { recursive: true });
    fs.cpSync(path.join(frontend, 'shared'), path.join(temporary, 'shared'), { recursive: true });
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
    global.window = dom.window;
    global.document = dom.window.document;
    global.localStorage = dom.window.localStorage;
    global.sessionStorage = dom.window.sessionStorage;
    global.FormData = dom.window.FormData;
    global.Option = dom.window.Option;
    global.Event = dom.window.Event;

    const auth = await import(pathToFileURL(path.join(temporary, 'patron', 'js', 'auth.js')));
    const submit = await import(pathToFileURL(path.join(temporary, 'patron', 'js', 'submit.js')));
    let pendingSubmission = null;
    let submissionCount = 0;
    global.fetch = async (url, options = {}) => {
      const requestUrl = String(url);
      if (requestUrl.endsWith('/api/asap/patron/login')) {
        const payload = JSON.parse(options.body);
        return response(200, loginBody(payload.username, `token-${payload.username}`));
      }
      if (requestUrl.endsWith('/api/asap/patron/logout')) return response(204, null);
      if (requestUrl.endsWith('/api/asap/patron/suggestions')) {
        submissionCount++;
        return new Promise((resolve, reject) => { pendingSubmission = { resolve, reject }; });
      }
      throw new Error(`Unexpected request: ${requestUrl}`);
    };

    async function login(barcode) {
      document.getElementById('barcode').value = barcode;
      document.getElementById('pin').value = '1234';
      await auth.handleLoginSubmit({ preventDefault() {} });
      assert.equal(document.getElementById('step-form').classList.contains('hidden'), false);
    }

    function fillDraft(barcode) {
      document.getElementById('title').value = `${barcode} draft title`;
      document.getElementById('author').value = `${barcode} draft author`;
      document.getElementById('isbn').value = '9780000000001';
      document.getElementById('preferred-pickup-branch').value = '101';
    }

    function readOwnerState() {
      return {
        displayedBarcode: document.getElementById('display-barcode').textContent,
        formVisible: !document.getElementById('step-form').classList.contains('hidden'),
        title: document.getElementById('title').value,
        author: document.getElementById('author').value,
        isbn: document.getElementById('isbn').value,
        pickup: document.getElementById('preferred-pickup-branch').value,
        errorVisible: !document.getElementById('submit-error').classList.contains('hidden'),
        error: document.getElementById('submit-error').textContent,
        submitDisabled: document.getElementById('submit-btn').disabled,
        submitLabel: document.getElementById('submit-btn').textContent
      };
    }

    await login('20000000000901');
    for (const scenario of [
      { name: '201', response: response(201, { id: '9007199254740993', successTitle: 'A', successMessage: 'A' }) },
      { name: '409', response: response(409, { message: 'A conflict', conflictTitle: 'A conflict' }) },
      { name: '400', response: response(400, { message: 'A validation error' }) },
      { name: '500', response: response(500, { message: 'A server error' }) },
      { name: 'cancel', abort: true }
    ]) {
      fillDraft('20000000000901');
      const submitA = submit.handleSuggestionSubmit({ preventDefault() {} });
      await until(() => pendingSubmission !== null, `A's ${scenario.name} submission did not start`);

      await auth.logout();
      await login('20000000000902');
      fillDraft('20000000000902');
      const expected = readOwnerState();

      const oldRequest = pendingSubmission;
      pendingSubmission = null;
      if (scenario.abort) {
        oldRequest.reject(Object.assign(new Error('Request aborted'), { name: 'AbortError' }));
      } else {
        oldRequest.resolve(scenario.response);
      }
      await submitA;

      assert.deepEqual(readOwnerState(), expected,
        `A's stale ${scenario.name} completion must not change B's form, stage, feedback, or busy state`);
      assert.equal(sessionStorage.getItem('asap_patron_token'), 'token-20000000000902',
        `A's stale ${scenario.name} completion must not replace B's token`);

      await auth.logout();
      if (scenario.name !== 'cancel') await login('20000000000901');
    }

    await login('20000000000901');
    // A current submission with an invalid 201 body may already be committed. Keep its
    // submit action closed to prevent a blind second POST until the user establishes a new session.
    fillDraft('20000000000901');
    const malformed = submit.handleSuggestionSubmit({ preventDefault() {} });
    await until(() => pendingSubmission !== null, 'Malformed-response submission did not start');
    const malformedRequest = pendingSubmission;
    pendingSubmission = null;
    malformedRequest.resolve(response(201, null, new SyntaxError('truncated success JSON')));
    await malformed;
    assert.equal(document.getElementById('submit-btn').disabled, true);
    assert.match(document.getElementById('submit-error').textContent, /Please do not submit again/);
    const requestsBeforeRepeat = submissionCount;
    await submit.handleSuggestionSubmit({ preventDefault() {} });
    assert.equal(submissionCount, requestsBeforeRepeat, 'unknown committed outcomes cannot trigger a duplicate POST');

    await auth.logout();
    await login('20000000000903');
    assert.equal(document.getElementById('submit-btn').disabled, false,
      'a new session clears the previous identity\'s unknown submit state');
  } finally {
    dom.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
  console.log('Patron submission session ownership and unknown completion regressions passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
