const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');
const { patronSession } = require('./helpers/patron-session-fixture');

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
  return patronSession(barcode, token);
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
    for (const scenario of [
      { code: 'submission_configuration_changed', message: 'The suggestion form changed. Refresh the form and try again.' },
      { code: 'material_format_changed', message: 'The selected material format changed. Refresh the form and try again.' },
      { code: 'request_not_created_pickup_changed', message: 'The pickup preference changed, but the suggestion was not created.' }
    ]) {
      fillDraft('20000000000901');
      const before = readOwnerState();
      const rejected = submit.handleSuggestionSubmit({ preventDefault() {} });
      await until(() => pendingSubmission !== null, `${scenario.code} submission did not start`);
      const dispatched = pendingSubmission;
      pendingSubmission = null;
      dispatched.resolve(response(409, scenario));
      await rejected;
      assert.equal(document.getElementById('step-form').classList.contains('hidden'), false,
        `${scenario.code} must keep the draft in the form instead of claiming an existing submission.`);
      assert.equal(document.getElementById('step-conflict').classList.contains('hidden'), true);
      assert.equal(document.getElementById('submit-error').textContent, scenario.message);
      assert.equal(document.activeElement, document.getElementById('submit-error'));
      assert.equal(document.getElementById('title').value, before.title);
      assert.equal(document.getElementById('submit-btn').disabled, false);
      await auth.logout();
      await login('20000000000901');
    }

    fillDraft('20000000000901');
    const duplicateSubmission = submit.handleSuggestionSubmit({ preventDefault() {} });
    await until(() => pendingSubmission !== null, 'The real duplicate control did not start');
    const duplicateRequest = pendingSubmission;
    pendingSubmission = null;
    duplicateRequest.resolve(response(409, {
      message: 'This patron already has this suggestion.', conflictTitle: 'Already Submitted',
      conflictMessage: 'This title is already being reviewed.',
      duplicate: { id: '9007199254740993', status: 'suggestion', title: 'Existing title' }
    }));
    await duplicateSubmission;
    assert.equal(document.getElementById('step-conflict').classList.contains('hidden'), false);
    assert.equal(document.getElementById('conflict-title').textContent, 'Already Submitted');
    await auth.logout();
    await login('20000000000901');

    for (const id of ['9007199254740993', '9223372036854775807']) {
      fillDraft('20000000000901');
      const accepted = submit.handleSuggestionSubmit({ preventDefault() {} });
      await until(() => pendingSubmission !== null, `Valid SQL bigint ID ${id} submission did not start`);
      const acceptedRequest = pendingSubmission;
      pendingSubmission = null;
      acceptedRequest.resolve(response(201, { id, successTitle: 'Saved', successMessage: 'Saved' }));
      await accepted;
      assert.equal(document.getElementById('step-success').classList.contains('hidden'), false,
        `Canonical SQL bigint ID ${id} from HTTP 201 remains a confirmed submission.`);
      await auth.logout();
      await login('20000000000901');
    }

    // Successful status alone is not evidence that the server's SQL bigint receipt is valid.
    // Each malformed 2xx could already have committed, so keep it unknown and block a blind retry.
    const malformedSuccesses = [
      { name: 'zero ID', status: 201, id: '0' },
      { name: 'noncanonical ID', status: 201, id: '01' },
      { name: 'overflow ID', status: 201, id: '9223372036854775808' },
      { name: 'numeric ID', status: 201, id: 9007199254740993 },
      { name: 'unexpected HTTP 200', status: 200, id: '9223372036854775807' },
      { name: 'unexpected HTTP 202', status: 202, id: '9007199254740993' },
      { name: 'malformed JSON', status: 201, parseError: new SyntaxError('truncated success JSON') }
    ];
    for (const [index, scenario] of malformedSuccesses.entries()) {
      fillDraft('20000000000901');
      const pending = submit.handleSuggestionSubmit({ preventDefault() {} });
      await until(() => pendingSubmission !== null, `${scenario.name} submission did not start`);
      const dispatched = pendingSubmission;
      pendingSubmission = null;
      const body = { id: scenario.id, successTitle: 'Saved', successMessage: 'Saved' };
      dispatched.resolve(response(scenario.status, body, scenario.parseError || null));
      await pending;
      assert.equal(document.getElementById('step-success').classList.contains('hidden'), true,
        `${scenario.name} must not render a confirmed submission.`);
      assert.equal(document.getElementById('submit-btn').disabled, true,
        `${scenario.name} may have committed and must block an automatic second POST.`);
      assert.match(document.getElementById('submit-error').textContent, /Please do not submit again/,
        `${scenario.name} must explain that the outcome is unconfirmed.`);
      const requestsBeforeRepeat = submissionCount;
      await submit.handleSuggestionSubmit({ preventDefault() {} });
      assert.equal(submissionCount, requestsBeforeRepeat,
        `${scenario.name}: unknown committed outcomes cannot trigger a duplicate POST`);
      if (index < malformedSuccesses.length - 1) {
        await auth.logout();
        await login('20000000000901');
      }
    }

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
