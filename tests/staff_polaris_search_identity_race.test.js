const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

const response = (status, body) => ({
  ok: status < 400,
  status,
  statusText: status < 400 ? 'OK' : 'Request failed',
  json: async () => body
});

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

async function settle() {
  for (let attempt = 0; attempt < 5; attempt += 1) {
    await new Promise(resolve => setImmediate(resolve));
  }
}

(async () => {
  const root = path.resolve(__dirname, '..');
  const frontend = path.join(root, 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-polaris-race-'));
  let dom;
  try {
    fs.cpSync(path.join(frontend, 'staff'), path.join(temporary, 'staff'), { recursive: true });
    fs.cpSync(path.join(frontend, 'shared'), path.join(temporary, 'shared'), { recursive: true });
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
    dom = new JSDOM(fs.readFileSync(path.join(frontend, 'staff', 'index.html'), 'utf8'), {
      url: 'https://localhost/staff/'
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.CustomEvent = dom.window.CustomEvent;
    global.Event = dom.window.Event;
    global.FormData = dom.window.FormData;
    global.Node = dom.window.Node;
    global.Option = dom.window.Option;
    global.localStorage = dom.window.localStorage;
    global.navigator = dom.window.navigator;
    global.requestAnimationFrame = callback => callback();
    dom.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    dom.window.HTMLDialogElement.prototype.close = function () {
      this.open = false;
      this.dispatchEvent(new dom.window.Event('close'));
    };
    const dialog = document.getElementById('polarisSearchDialog');

    const lookups = [];
    const actions = [];
    const id = '9007199254740993';
    const title = { type: 'title_request', id, title: 'Collision title', libraryOrgId: '2' };
    const copy = { type: 'additional_copy', id, title: 'Collision copy', libraryOrgId: '2' };
    const persisted = { title: 'Collision title', copy: 'Collision copy' };
    global.fetch = (url, options = {}) => {
      if (String(url).endsWith('/bib-lookup')) {
        const pending = deferred();
        lookups.push({ body: JSON.parse(options.body), pending, signal: options.signal });
        return pending.promise;
      }
      if (String(url).includes('/title-requests/') && String(url).endsWith('/action')) {
        actions.push({ url: String(url), body: JSON.parse(options.body) });
        persisted.title = actions.at(-1).body.title;
        return Promise.resolve(response(200, { type: 'title_request', id, title: persisted.title, status: 'pending_hold' }));
      }
      throw new Error(`Unexpected request: ${url}`);
    };

    const state = await import(pathToFileURL(path.join(temporary, 'staff/js/state.js')).href);
    const search = await import(pathToFileURL(path.join(temporary, 'staff/js/modals/polaris-search.js')).href);
    state.setStaffSession({
      authenticated: true, accessAllowed: true, antiforgeryToken: 'test-token',
      staff: { id: '7', role: 'super_admin', organizationId: 1, userPrincipalName: 'staff@example.org' }
    });
    const ctx = { staffSession: state.staffSession, publicationOptions: ['Already published'], holdingsLookupUnavailable: false };
    const results = () => document.getElementById('polaris-search-results');
    const status = () => document.getElementById('polaris-search-status').textContent;
    const completeSearch = async (index, label) => {
      lookups[index].pending.resolve(response(200, {
        status: 'ok', results: [{ title: label, bibId: `BIB-${label}` }], totalMatches: 1
      }));
      await settle();
    };
    const completeHoldings = async (index) => {
      lookups[index].pending.resolve(response(200, {
        holdingsSummary: { isHoldable: true, myLibraryCount: 1, otherLibraryCount: 0, consortiumCount: 1 }
      }));
      await settle();
    };
    const open = (row) => search.openPolarisSearch(row, 'title', {}, ctx);

    // The same large ID exists in both tables. Search remains available for the copy.
    const copyOpen = open(copy);
    await settle();
    assert.equal(lookups[0].body.requestType, 'additional_copy');
    assert.equal(lookups[0].body.requestId, id);
    await completeSearch(0, 'Copy result');
    await copyOpen;
    assert.equal(lookups[1].body.requestType, 'additional_copy');
    assert.equal(lookups[1].body.requestId, id);
    assert.equal(lookups[1].body.bibId, 'BIB-Copy result');
    await completeHoldings(1);
    assert.equal(results().querySelector('#polaris-additional-copy-action'), null,
      'additional-copy rows must not expose title-request-only buy/queue action');
    const copyButtons = [...results().querySelectorAll('button')].filter(button => !button.disabled && !button.classList.contains('hidden'));
    for (const button of copyButtons) {
      button.click();
      await settle();
    }
    assert.equal(actions.length, 0, 'additional-copy result controls must never mutate the colliding title request');
    assert.equal(persisted.title, 'Collision title');
    assert.equal(persisted.copy, 'Collision copy');
    dialog.close();

    const titleOpen = open(title);
    await settle();
    assert.equal(lookups[2].body.requestType, 'title_request');
    assert.equal(lookups[2].body.requestId, id);
    await completeSearch(2, 'Title result');
    await titleOpen;
    assert.equal(lookups[3].body.requestType, 'title_request');
    assert.equal(lookups[3].body.requestId, id);
    await completeHoldings(3);
    assert.equal(results().querySelector('#polaris-additional-copy-action')?.disabled, false,
      'the established title-request buy/queue control remains available');
    const queueButton = [...results().querySelectorAll('button')].find(button => button.textContent === 'Use BIB & Queue Now');
    assert.ok(queueButton && !queueButton.disabled, 'title-request action remains available');
    queueButton.click();
    await settle();
    assert.equal(actions.length, 1);
    assert.equal(actions[0].url, `/api/asap/staff/title-requests/${id}/action`);
    assert.equal(persisted.title, 'Title result');
    assert.equal(persisted.copy, 'Collision copy');

    // An older request with the same numeric ID must not replace a newer type context.
    const oldOpen = open(title);
    await settle();
    const newOpen = open(copy);
    await settle();
    assert.equal(lookups[5].body.requestType, 'additional_copy');
    await completeSearch(5, 'Newest copy');
    await newOpen;
    await completeHoldings(6);
    await completeSearch(4, 'Stale title');
    await oldOpen;
    assert.match(results().textContent, /Newest copy/);
    assert.doesNotMatch(results().textContent, /Stale title/);
    assert.equal(results().querySelectorAll('button:not(.hidden)').length, 0);

    // A rapid rerun supersedes the previous query in the same dialog.
    const input = document.getElementById('polaris-search-input');
    input.value = 'query one';
    document.getElementById('polaris-search-rerun-btn').click();
    await settle();
    input.value = 'query two';
    document.getElementById('polaris-search-rerun-btn').click();
    await settle();
    assert.equal(lookups[8].body.query, 'query two');
    await completeSearch(8, 'Query two');
    await completeSearch(7, 'Query one');
    assert.match(results().textContent, /Query two/);
    assert.doesNotMatch(results().textContent, /Query one/);

    // A late error also cannot replace a newer successful search.
    input.value = 'old error';
    document.getElementById('polaris-search-rerun-btn').click();
    await settle();
    input.value = 'new success';
    document.getElementById('polaris-search-rerun-btn').click();
    await settle();
    await completeSearch(11, 'New success');
    lookups[10].pending.resolve(response(502, { message: 'Old gateway failure' }));
    await settle();
    assert.match(results().textContent, /New success/);
    assert.doesNotMatch(status(), /Old gateway failure/);

    // Closing invalidates pending work before the dialog is opened for another row.
    input.value = 'closing search';
    document.getElementById('polaris-search-rerun-btn').click();
    await settle();
    dialog.close();
    const reopened = open(title);
    await settle();
    await completeSearch(14, 'Reopened title');
    await reopened;
    await completeSearch(13, 'Closed stale copy');
    assert.match(results().textContent, /Reopened title/);
    assert.doesNotMatch(results().textContent, /Closed stale copy/);
    assert.equal(lookups[14].body.requestType, 'title_request');
    assert.equal(actions.length, 1, 'stale responses must not dispatch an action');
    await completeHoldings(15);
    const currentAction = [...results().querySelectorAll('button')].find(button => button.textContent === 'Use BIB & Queue Now');
    assert.ok(currentAction && !currentAction.disabled);
    currentAction.click();
    await settle();
    assert.equal(actions.length, 2, 'the visible action belongs to the latest type-qualified context');
    assert.equal(actions[1].url, `/api/asap/staff/title-requests/${id}/action`);
    assert.equal(actions[1].body.title, 'Reopened title');

    for (const identity of [
      { type: 'additional_copy', id }, { type: 'unknown', id }, { id }, { type: 'title_request', id: '' }
    ]) {
      assert.equal(await search.performImmediateStaffAction(identity, { action: 'catalogFound' }, ctx), false);
    }
    assert.equal(actions.length, 2, 'invalid type or missing ID must fail closed at the mutation boundary');

    const newFormOpen = search.openPolarisSearch({ id: '', title: 'New suggestion', libraryOrgId: '2' }, 'title', { source: 'new' }, ctx);
    await settle();
    await completeSearch(16, 'New form result');
    await newFormOpen;
    assert.ok([...results().querySelectorAll('button')].some(button => button.textContent === 'Apply to Form'));
    assert.equal([...results().querySelectorAll('button')].some(button => button.textContent === 'Use BIB & Queue Now'), false);
    assert.equal(actions.length, 2, 'a new suggestion has no persisted title request to mutate');

    const oldTitleSearch = lookups.length;
    const oldTitleOpen = open(title);
    await settle();
    await completeSearch(oldTitleSearch, 'Old holdings title');
    await oldTitleOpen;
    const oldHoldings = lookups.length - 1;
    const detachedAction = [...results().querySelectorAll('button')].find(button => button.textContent === 'Use BIB & Queue Now');
    assert.ok(detachedAction && detachedAction.disabled);
    const latestCopySearch = lookups.length;
    const latestCopyOpen = open(copy);
    await settle();
    await completeSearch(latestCopySearch, 'Latest holdings copy');
    await latestCopyOpen;
    assert.equal(lookups[oldHoldings].signal.aborted, true, 'superseded holdings are aborted');
    await completeHoldings(oldHoldings); // Model a provider that completes despite abort.
    assert.equal(detachedAction.disabled, true, 'old holdings cannot enable a stale action');
    detachedAction.click();
    await settle();
    assert.match(results().textContent, /Latest holdings copy/);
    assert.equal(actions.length, 2, 'a detached stale action cannot mutate the old request');

    const replacedSessionSearch = lookups.length;
    document.getElementById('polaris-search-input').value = 'session replaced';
    document.getElementById('polaris-search-rerun-btn').click();
    await settle();
    state.setStaffSession({ authenticated: false, antiforgeryToken: 'test-token' });
    state.setStaffSession({
      authenticated: true, accessAllowed: true, antiforgeryToken: 'new-session-token',
      staff: { id: '7', role: 'super_admin', organizationId: 1, userPrincipalName: 'staff@example.org' }
    });
    await completeSearch(replacedSessionSearch, 'Previous session');
    assert.equal(results().querySelectorAll('button').length, 0,
      'sign-out and sign-in as the same staff invalidates pending dialog actions');
    assert.equal(actions.length, 2);

    const validRefreshSearch = lookups.length;
    const validRefreshOpen = open(title);
    await settle();
    state.setStaffSession({
      authenticated: true, accessAllowed: true, antiforgeryToken: 'refreshed-token',
      staff: { id: '7', role: 'super_admin', organizationId: 1, userPrincipalName: 'staff@example.org' }
    });
    await completeSearch(validRefreshSearch, 'Valid refreshed session');
    await validRefreshOpen;
    assert.match(results().textContent, /Valid refreshed session/,
      'a token refresh for the same current staff must not discard the search');
    document.getElementById('polaris-search-input').value = '';
    document.getElementById('polaris-search-rerun-btn').click();
    await settle();
    assert.equal(results().querySelectorAll('button').length, 0,
      'empty rerun clears actions from the preceding result set');
  } finally {
    dom?.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
  console.log('Staff Polaris identity and race tests passed.');
})().catch(error => { console.error(error); process.exitCode = 1; });
