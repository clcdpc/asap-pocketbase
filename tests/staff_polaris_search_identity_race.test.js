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
  const frontend = process.env.ASAP_FRONTEND_TEST_ROOT || path.join(root, 'src', 'Asap.Web', 'Frontend');
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
    let queueCloseEvents = false;
    let polarisCloseEvents = 0;
    dom.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    dom.window.HTMLDialogElement.prototype.close = function () {
      this.open = false;
      const dispatchClose = () => {
        if (this.id === 'polarisSearchDialog') polarisCloseEvents += 1;
        this.dispatchEvent(new dom.window.Event('close'));
      };
      if (queueCloseEvents) {
        setTimeout(dispatchClose, 0);
      } else {
        dispatchClose();
      }
    };
    const dialog = document.getElementById('polarisSearchDialog');

    const lookups = [];
    const actions = [];
    const outbound = [];
    let failNextAction = false;
    let deferNextAction = false;
    const id = '9007199254740993';
    const title = { type: 'title_request', id, title: 'Collision title', status: 'suggestion', libraryOrgId: '2' };
    const copy = { type: 'additional_copy', id, title: 'Collision copy', libraryOrgId: '2' };
    const persisted = { title: 'Collision title', copy: 'Collision copy' };
    global.fetch = (url, options = {}) => {
      outbound.push({ url: String(url), method: options.method || 'GET' });
      if (String(url).endsWith('/bib-lookup')) {
        const pending = deferred();
        lookups.push({ body: JSON.parse(options.body), pending, signal: options.signal });
        return pending.promise;
      }
      if (String(url).includes('/title-requests/') && String(url).endsWith('/action')) {
        if (failNextAction) {
          failNextAction = false;
          return Promise.resolve(response(503, { message: 'Action temporarily unavailable.' }));
        }
        const action = { url: String(url), body: JSON.parse(options.body), pending: deferred() };
        actions.push(action);
        if (!deferNextAction) {
          persisted.title = action.body.title;
          action.pending.resolve(response(200, {
            request: { type: 'title_request', id, title: persisted.title, status: 'pending_hold', version: `version-${actions.length}` },
            additionalCopyRequestId: action.body.action === 'additionalCopy' ? `copy-${actions.length}` : null,
            purchaseReminderEmail: { requested: !!action.body.emailPurchaseReminder, queued: !!action.body.emailPurchaseReminder }
          }));
        }
        deferNextAction = false;
        return action.pending.promise;
      }
      if (String(url).includes('/title-requests/') && String(url).endsWith('/claim')) {
        return Promise.resolve(response(200, { ...title, version: 'claimed-version' }));
      }
      if (String(url).includes('/title-requests?') || String(url).includes('/additional-copies?')) {
        return Promise.resolve(response(200, { items: [], scope: 'all', availableLibraries: [] }));
      }
      throw new Error(`Unexpected request: ${url}`);
    };

    const state = await import(pathToFileURL(path.join(temporary, 'staff/js/state.js')).href);
    const search = await import(pathToFileURL(path.join(temporary, 'staff/js/modals/polaris-search.js')).href);
    const editSubmit = await import(pathToFileURL(path.join(temporary, 'staff/js/modals/edit-submit.js')).href);
    const rowActions = await import(pathToFileURL(path.join(temporary, 'staff/js/actions.js')).href);
    const gridActions = await import(pathToFileURL(path.join(temporary, 'staff/js/grid-actions.js')).href);
    const gridRendering = await import(pathToFileURL(path.join(temporary, 'staff/js/grid-rendering.js')).href);
    const editPickup = await import(pathToFileURL(path.join(temporary, 'staff/js/edit-pickup.js')).href);
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
    const completeHoldings = async (index, count = 1) => {
      lookups[index].pending.resolve(response(200, {
        holdingsSummary: { isHoldable: true, myLibraryCount: count, otherLibraryCount: 0, consortiumCount: count }
      }));
      await settle();
    };
    const completeMultiSearch = async index => {
      lookups[index].pending.resolve(response(200, {
        status: 'ok', totalMatches: 2, results: [
          { title: 'First multi result', bibId: '9001', identifier: '9781111111111' },
          { title: 'Second multi result', bibId: '9002' }
        ]
      }));
      await settle();
    };
    const flushDialogCloseEvents = async () => {
      await new Promise(resolve => setTimeout(resolve, 10));
      await settle();
    };
    const finishAction = async action => {
      persisted.title = action.body.title;
      action.pending.resolve(response(200, {
        request: { type: 'title_request', id, title: persisted.title, status: 'pending_hold', version: `version-${actions.indexOf(action) + 1}` },
        additionalCopyRequestId: `copy-${actions.indexOf(action) + 1}`,
        purchaseReminderEmail: { requested: !!action.body.emailPurchaseReminder, queued: !!action.body.emailPurchaseReminder }
      }));
      await settle();
    };
    const open = (row) => search.openPolarisSearch(row, 'title', {}, ctx);

    const oldContext = open({ type: 'title_request', id: '9007199254740994', title: 'Old context', libraryOrgId: '2' });
    await settle();
    const latestContext = open({ type: 'title_request', id: '9007199254740995', title: 'Latest context', libraryOrgId: '2' });
    await settle();
    await completeSearch(1, 'Latest context result');
    await latestContext;
    await completeSearch(0, 'Old context result');
    await oldContext;
    assert.match(results().textContent, /Latest context result/);
    assert.doesNotMatch(results().textContent, /Old context result/,
      'an older dialog invocation cannot replace a newer result');
    dialog.close();
    lookups.length = 0;

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
    assert.equal(results().querySelector('.polaris-additional-copy-action'), null,
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
    assert.equal(results().querySelector('.polaris-additional-copy-action')?.disabled, false,
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
    for (const identity of [
      { type: 'additional_copy', id }, { type: 'unknown', id }, { id },
      { type: 'title_request', id: '' }, id
    ]) {
      const dispatch = editSubmit.submitTitleRequestAction(identity, { action: 'catalogFound' });
      await settle();
      assert.equal(actions.length, 2, 'invalid identity must not emit a title-request mutation');
      assert.equal(await Promise.race([dispatch, new Promise(resolve => setTimeout(resolve, 10))]), false);
    }
    assert.equal(actions.length, 2, 'the exported action helper must require an explicit title-request type');

    state.setCurrentSuggestions([title, copy]);
    state.setAllSuggestions([title, copy]);
    const assertNoMixedDispatch = async invoke => {
      const before = outbound.length;
      const attempt = invoke();
      await settle();
      if (document.getElementById('confirm-dialog')?.open) {
        document.getElementById('confirm-dialog-ok').click();
        await settle();
      }
      assert.equal(outbound.length, before, 'mixed-context operation emitted a request');
      await Promise.race([attempt, new Promise(resolve => setTimeout(resolve, 10))]);
    };
    const mixedCtx = { currentSuggestions: [title, copy], allSuggestions: [title, copy], staffSession: state.staffSession };
    await assertNoMixedDispatch(() => rowActions.undoRow({ type: 'unknown', id }));
    await assertNoMixedDispatch(() => rowActions.deleteClosedRequest({ id }));
    await assertNoMixedDispatch(() => rowActions.closeDuplicateRequest({ type: 'unknown', id }, { alreadyConfirmed: true }));
    await assertNoMixedDispatch(() => gridActions.mutateRequestClaim({ type: 'unknown', id }, 'claim', '', mixedCtx));
    await assertNoMixedDispatch(() => gridActions.openAssignDialog({ type: 'unknown', id }));
    await assertNoMixedDispatch(() => gridActions.buyAnotherCopyForRow(copy, mixedCtx));
    await assertNoMixedDispatch(() => gridActions.runRowActionDescriptor(copy, { key: 'buyAnotherCopy' }, mixedCtx));
    await assertNoMixedDispatch(() => gridActions.closeAdditionalCopyRequest(id));
    await assertNoMixedDispatch(() => editPickup.loadEditPickupForRequest({ type: 'unknown', id }));
    assert.match(gridRendering.rowMarker({ type: 'unknown', id }), /data-request-type="unknown"/,
      'rendered row identity must retain an unknown type for the event guard');

    const newFormOpen = search.openPolarisSearch({ id: '', title: 'New suggestion', libraryOrgId: '2' }, 'title', { source: 'new' }, ctx);
    await settle();
    await completeSearch(16, 'New form result');
    await newFormOpen;
    assert.ok([...results().querySelectorAll('button')].some(button => button.textContent === 'Apply to Form'));
    assert.equal([...results().querySelectorAll('button')].some(button => button.textContent === 'Use BIB & Queue Now'), false);
    await completeHoldings(17);
    assert.equal(results().querySelector('.polaris-additional-copy-action'), null,
      'qualifying holdings cannot create an immediate action for an unsaved suggestion');
    assert.equal(actions.length, 2, 'a new suggestion has no persisted title request to mutate');

    const oldTitleSearch = lookups.length;
    const oldTitleOpen = open(title);
    await settle();
    await completeSearch(oldTitleSearch, 'Old holdings title');
    await oldTitleOpen;
    const oldHoldings = lookups.length - 1;
    const detachedAction = [...results().querySelectorAll('button')].find(button => button.textContent === 'Use BIB & Queue Now');
    const detachedAdditionalCopyAction = results().querySelector('.polaris-additional-copy-action');
    assert.ok(detachedAction && detachedAction.disabled);
    assert.ok(detachedAdditionalCopyAction && detachedAdditionalCopyAction.disabled);
    const latestCopySearch = lookups.length;
    const latestCopyOpen = open(copy);
    await settle();
    await completeSearch(latestCopySearch, 'Latest holdings copy');
    await latestCopyOpen;
    assert.equal(lookups[oldHoldings].signal.aborted, true, 'superseded holdings are aborted');
    await completeHoldings(oldHoldings); // Model a provider that completes despite abort.
    assert.equal(detachedAction.disabled, true, 'old holdings cannot enable a stale action');
    assert.equal(detachedAdditionalCopyAction.disabled, true, 'old holdings cannot enable a stale additional-copy action');
    assert.equal(detachedAdditionalCopyAction.classList.contains('hidden'), true);
    detachedAction.click();
    detachedAdditionalCopyAction.dispatchEvent(new dom.window.Event('click'));
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

    const unownedEditSearch = lookups.length;
    const unownedEditOpen = search.openPolarisSearch(title, 'title', { source: 'edit' }, ctx);
    await settle();
    await completeSearch(unownedEditSearch, 'Unowned edit result');
    await unownedEditOpen;
    lookups[unownedEditSearch + 1].pending.resolve(response(200, {
      holdingsSummary: { isHoldable: false, myLibraryCount: 0, otherLibraryCount: 0, consortiumCount: 0 }
    }));
    await settle();
    const unownedEditAction = results().querySelector('.polaris-additional-copy-action');
    assert.ok(unownedEditAction && unownedEditAction.disabled && unownedEditAction.classList.contains('hidden'),
      'edit actions remain unavailable when the BIB has no consortium holdings');

    const runMultiResultChecks = async () => {
      const multiSearch = lookups.length;
      const multiOpen = open({ ...title, identifier: '9789999999999' });
      await settle();
      await completeMultiSearch(multiSearch);
      await multiOpen;
      let multiActions = [...results().querySelectorAll('.polaris-additional-copy-action')];
      assert.equal(multiActions.length, 2);
      assert.equal(new Set(multiActions.map(button => button.id)).size, 2);
      const resultIds = [...results().querySelectorAll('[id]')].map(element => element.id);
      assert.equal(new Set(resultIds).size, resultIds.length, 'result DOM IDs must be unique');
      assert.match(multiActions[0].getAttribute('aria-label'), /BIB 9001, First multi result/);
      assert.match(multiActions[1].getAttribute('aria-label'), /BIB 9002, Second multi result/);
      assert.deepEqual(lookups.slice(multiSearch + 1, multiSearch + 3).map(lookup => lookup.body.bibId), ['9001', '9002']);
      for (const button of multiActions) {
        assert.equal(button.disabled, true);
        assert.equal(button.classList.contains('hidden'), true);
      }
      await completeHoldings(multiSearch + 1);
      assert.equal(multiActions[0].disabled, false);
      assert.equal(multiActions[1].disabled, true, 'BIB A holdings cannot enable BIB B');
      await completeHoldings(multiSearch + 2, 2);
      assert.match(results().querySelectorAll('.polaris-search-result')[1].textContent, /Owned by you \(2\)/);
      assert.equal(multiActions[1].disabled, false);
      const confirmMultiAction = async button => {
        button.click();
        await settle();
        const confirmation = document.getElementById('confirm-additional-copy-reminder')?.closest('dialog');
        assert.ok(confirmation?.open);
        [...confirmation.querySelectorAll('button')].find(item => item.textContent === 'Confirm').click();
        await settle();
        return actions.at(-1).body;
      };
      const firstMultiAction = await confirmMultiAction(multiActions[0]);
      assert.equal(firstMultiAction.bibid, '9001');
      assert.equal(firstMultiAction.title, 'First multi result');
      assert.equal(firstMultiAction.identifier, '9781111111111');

      const secondMultiSearch = lookups.length;
      const secondMultiOpen = open({ ...title, identifier: '9789999999999' });
      await settle();
      await completeMultiSearch(secondMultiSearch);
      await secondMultiOpen;
      await completeHoldings(secondMultiSearch + 1);
      await completeHoldings(secondMultiSearch + 2, 2);
      multiActions = [...results().querySelectorAll('.polaris-additional-copy-action')];
      const secondMultiAction = await confirmMultiAction(multiActions[1]);
      assert.equal(secondMultiAction.bibid, '9002');
      assert.equal(secondMultiAction.title, 'Second multi result');
      assert.equal(secondMultiAction.identifier, null,
        'a result without an identifier cannot submit the source identifier as catalog evidence');

      const staleMultiSearch = lookups.length;
      const staleMultiOpen = open({ ...title, identifier: '9789999999999' });
      await settle();
      await completeMultiSearch(staleMultiSearch);
      await staleMultiOpen;
      const staleMultiActions = [...results().querySelectorAll('.polaris-additional-copy-action')];
      const replacementMultiSearch = lookups.length;
      const replacementMultiOpen = open(copy);
      await settle();
      await completeSearch(replacementMultiSearch, 'Replacement after multi result');
      await replacementMultiOpen;
      await completeHoldings(staleMultiSearch + 1);
      await completeHoldings(staleMultiSearch + 2);
      assert.ok(staleMultiActions.every(button => button.disabled && button.classList.contains('hidden')),
        'stale holdings cannot enable either detached result action');
      assert.equal(results().querySelectorAll('.polaris-additional-copy-action').length, 0);

      const repeatedBibSearch = lookups.length;
      const repeatedBibOpen = open(title);
      await settle();
      lookups[repeatedBibSearch].pending.resolve(response(200, {
        status: 'ok', totalMatches: 2, results: [
          { title: 'Repeated catalog row', bibId: '9001' },
          { title: 'Repeated catalog row', bibId: '9001' }
        ]
      }));
      await repeatedBibOpen;
      const repeatedActions = [...results().querySelectorAll('.polaris-additional-copy-action')];
      assert.equal(repeatedActions.length, 2);
      assert.equal(new Set(repeatedActions.map(button => button.id)).size, 2,
        'result index disambiguates repeated BIB IDs');
      assert.equal(new Set(repeatedActions.map(button => button.getAttribute('aria-label'))).size, 2,
        'repeated catalog rows still have distinct accessible names');
    };

    queueCloseEvents = true;
    ctx.currentSuggestions = [title, copy];
    ctx.allSuggestions = [];
    const editModal = document.getElementById('editModal');
    const editSearchButton = document.getElementById('edit-title-polaris-search');
    const editId = document.getElementById('edit-id');
    const linkedUrl = requestId => `/staff/?stage=submitted&request=${requestId}&requestType=title_request#settings-workflow`;
    const setLinkedUrl = requestId => window.history.replaceState(null, '', linkedUrl(requestId));
    setLinkedUrl(id);
    editId.value = id;
    editId.dataset.requestType = 'title_request';
    document.getElementById('edit-title').value = title.title;
    let editRefreshes = 0;
    const editRefreshModes = [];
    const launchEditSearch = () => search.launchEditPolarisSearch('title', editSearchButton, 'edit', ctx, options => {
      editRefreshes += 1;
      editRefreshModes.push(options?.silent === true ? 'silent' : 'normal');
      if (options?.silent !== true) document.getElementById('tab-desc').focus();
    });

    editModal.showModal();
    const manualSearch = lookups.length;
    launchEditSearch();
    await settle();
    await completeSearch(manualSearch, 'Manual close result');
    assert.equal(editModal.open, false, 'opening Polaris closes the parent edit dialog');
    search.closePolarisSearchDialog();
    await flushDialogCloseEvents();
    assert.equal(editModal.open, true, 'closing Polaris returns to the edit dialog');
    assert.equal(document.activeElement, editSearchButton, 'focus returns to the Polaris launch button');
    assert.equal(window.location.pathname + window.location.search + window.location.hash, linkedUrl(id),
      'manual Polaris close preserves the request deep link and unrelated URL state');

    const applySearch = lookups.length;
    launchEditSearch();
    await settle();
    await completeSearch(applySearch, 'Apply result');
    const applyButton = [...results().querySelectorAll('button')].find(button => button.textContent === 'Apply to Form');
    assert.ok(applyButton);
    applyButton.click();
    await flushDialogCloseEvents();
    assert.equal(dialog.open, false);
    assert.equal(editModal.open, true, 'Apply to Form returns to the edit dialog');
    assert.equal(document.getElementById('edit-bibid').value, 'BIB-Apply result');
    assert.equal(document.getElementById('selectedPolarisBibId').value, 'BIB-Apply result');
    assert.match(document.getElementById('edit-title').value, /Apply result/);
    assert.equal(document.activeElement, editSearchButton);

    const canceledSearch = lookups.length;
    launchEditSearch();
    await settle();
    await completeSearch(canceledSearch, 'Canceled result');
    await completeHoldings(canceledSearch + 1);
    results().querySelector('.polaris-additional-copy-action').click();
    await settle();
    const canceledConfirmation = document.getElementById('confirm-additional-copy-reminder')?.closest('dialog');
    [...canceledConfirmation.querySelectorAll('button')].find(button => button.textContent === 'Cancel').click();
    await settle();
    assert.equal(actions.length, 2, 'canceling confirmation cannot mutate the title request');
    assert.equal(dialog.open, true, 'canceling confirmation keeps Polaris open');
    search.closePolarisSearchDialog();
    await flushDialogCloseEvents();
    assert.equal(editModal.open, true, 'canceling confirmation preserves the return-to-edit path');

    const failedSearch = lookups.length;
    launchEditSearch();
    await settle();
    await completeSearch(failedSearch, 'Failed result');
    await completeHoldings(failedSearch + 1);
    results().querySelector('.polaris-additional-copy-action').click();
    await settle();
    failNextAction = true;
    const failedConfirmation = document.getElementById('confirm-additional-copy-reminder')?.closest('dialog');
    [...failedConfirmation.querySelectorAll('button')].find(button => button.textContent === 'Confirm').click();
    await settle();
    assert.equal(document.getElementById('alert-dialog').open, true, 'a failed mutation shows the existing error');
    assert.equal(dialog.open, true, 'a failed mutation keeps Polaris available');
    assert.equal(actions.length, 2);
    document.getElementById('alert-dialog-ok').click();
    await settle();
    search.closePolarisSearchDialog();
    await flushDialogCloseEvents();
    assert.equal(editModal.open, true, 'a failed mutation does not suppress return to edit');

    const editSearch = lookups.length;
    launchEditSearch();
    await settle();
    await completeSearch(editSearch, 'Edit result');
    const editAction = results().querySelector('.polaris-additional-copy-action');
    assert.ok(editAction, 'a persisted title request opened from edit retains the additional-copy action');
    assert.equal(editAction.disabled, true);
    assert.equal(editAction.classList.contains('hidden'), true);
    assert.ok([...results().querySelectorAll('button')].some(button => button.textContent === 'Apply to Form'));
    assert.equal([...results().querySelectorAll('button')].some(button => button.textContent === 'Use BIB & Queue Now'), false);
    await completeHoldings(editSearch + 1);
    assert.equal(editAction.disabled, false);
    assert.equal(editAction.classList.contains('hidden'), false);
    editAction.click();
    await settle();
    const confirmation = document.getElementById('confirm-additional-copy-reminder')?.closest('dialog');
    assert.ok(confirmation, 'the edit action uses the existing confirmation flow');
    document.getElementById('confirm-additional-copy-reminder').checked = true;
    const closeEventsBeforeAction = polarisCloseEvents;
    [...confirmation.querySelectorAll('button')].find(button => button.textContent === 'Confirm').click();
    await settle();
    await flushDialogCloseEvents();
    assert.equal(actions.length, 3);
    assert.equal(actions[2].url, `/api/asap/staff/title-requests/${id}/action`);
    assert.equal(actions[2].body.action, 'additionalCopy');
    assert.equal(actions[2].body.emailPurchaseReminder, true);
    assert.equal(actions[2].body.title, 'Edit result');
    assert.equal(persisted.copy, 'Collision copy');
    assert.equal(dialog.open, false, 'the successful action closes Polaris');
    assert.equal(polarisCloseEvents, closeEventsBeforeAction + 1, 'the queued Polaris close event fired');
    assert.equal(editModal.open, false, 'the queued close event must not reopen the stale edit dialog');
    assert.equal(editRefreshes, 1, 'the successful action refreshes the staff grid');
    assert.equal(editRefreshModes.at(-1), 'normal');
    assert.equal(window.location.search, '?stage=submitted', 'owned success clears only the matching request selection');
    assert.equal(window.location.hash, '#settings-workflow');
    assert.equal(document.activeElement, document.getElementById('tab-desc'),
      'owned success restores normal refresh focus');
    assert.match(document.getElementById('toast-container').textContent, /Additional-copy task created/);

    const staleEditSearch = lookups.length;
    const staleEditOpen = search.openPolarisSearch(title, 'title', { source: 'edit' }, ctx);
    await settle();
    await completeSearch(staleEditSearch, 'Stale edit result');
    await staleEditOpen;
    await completeHoldings(staleEditSearch + 1);
    results().querySelector('.polaris-additional-copy-action').click();
    await settle();
    const staleConfirmation = document.getElementById('confirm-additional-copy-reminder')?.closest('dialog');
    assert.ok(staleConfirmation);
    const replacementSearch = lookups.length;
    const replacementOpen = open(copy);
    await settle();
    await completeSearch(replacementSearch, 'Replacement copy result');
    await replacementOpen;
    await completeHoldings(replacementSearch + 1);
    [...staleConfirmation.querySelectorAll('button')].find(button => button.textContent === 'Confirm').click();
    await settle();
    assert.equal(actions.length, 3, 'confirmation after dialog replacement cannot mutate the older title request');

    for (const [row, options] of [
      [{ type: 'additional_copy', id, title: 'Copy edit', libraryOrgId: '2' }, { source: 'edit' }],
      [{ type: 'unknown', id, title: 'Unknown type', libraryOrgId: '2' }, {}],
      [{ type: 'title_request', id: '  ', title: 'Unsaved title', libraryOrgId: '2' }, {}],
      [{ ...title, status: 'hold_placed' }, {}],
      [{ ...title, status: 'closed' }, {}],
      [title, { source: 'new' }]
    ]) {
      const searchIndex = lookups.length;
      const pendingOpen = search.openPolarisSearch(row, 'title', options, ctx);
      await settle();
      await completeSearch(searchIndex, row.title);
      await pendingOpen;
      await completeHoldings(searchIndex + 1);
      assert.equal(results().querySelector('.polaris-additional-copy-action'), null,
        `invalid immediate-action context ${row.type}/${options.source || 'row'} must stay unavailable`);
    }
    assert.equal(actions.length, 3);

    search.closePolarisSearchDialog();
    await flushDialogCloseEvents();
    const { closeOpenDialogs } = await import(pathToFileURL(path.join(temporary, 'staff/js/dialogs.js')).href);
    const openReadyEdit = async label => {
      editId.value = id;
      editId.dataset.requestType = 'title_request';
      document.getElementById('edit-title').value = title.title;
      if (!editModal.open) editModal.showModal();
      const index = lookups.length;
      launchEditSearch();
      await settle();
      await completeSearch(index, label);
      await completeHoldings(index + 1);
      return results().querySelector('.polaris-additional-copy-action');
    };
    const confirmPendingAction = async button => {
      deferNextAction = true;
      button.click();
      await settle();
      const confirmation = document.getElementById('confirm-additional-copy-reminder')?.closest('dialog');
      assert.ok(confirmation);
      [...confirmation.querySelectorAll('button')].find(item => item.textContent === 'Confirm').click();
      await settle();
      return actions.at(-1);
    };

    // A committed response after manual close still refreshes data but cannot close the returned edit form.
    const manuallyClosedAction = await confirmPendingAction(await openReadyEdit('Manual pending action'));
    assert.equal(dialog.open, true);
    search.closePolarisSearchDialog();
    await flushDialogCloseEvents();
    assert.equal(editModal.open, true);
    editSearchButton.focus();
    const manualUrl = window.location.href;
    const refreshesBeforeManualCompletion = editRefreshes;
    await finishAction(manuallyClosedAction);
    assert.equal(editModal.open, true);
    assert.equal(editRefreshes, refreshesBeforeManualCompletion + 1);
    assert.equal(editRefreshModes.at(-1), 'silent');
    assert.equal(window.location.href, manualUrl);
    assert.equal(document.activeElement, editSearchButton, 'stale completion keeps the returned edit form focused');
    assert.match(document.getElementById('toast-container').textContent, /Additional-copy task created/);

    // A newer Polaris invocation owns the reused dialog and its return listener.
    const olderAction = await confirmPendingAction(await openReadyEdit('Older pending action'));
    search.closePolarisSearchDialog();
    await flushDialogCloseEvents();
    const newerSearch = lookups.length;
    launchEditSearch();
    await settle();
    await completeSearch(newerSearch, 'Newer Polaris invocation');
    await finishAction(olderAction);
    assert.equal(dialog.open, true, 'old success cannot close a newer Polaris invocation');
    assert.match(results().textContent, /Newer Polaris invocation/);
    search.closePolarisSearchDialog();
    await flushDialogCloseEvents();
    assert.equal(editModal.open, true, 'the newer return listener survives the old completion');

    // Reusing the edit DOM for another request must not let an old completion close it.
    const differentEditAction = await confirmPendingAction(await openReadyEdit('Different edit pending'));
    search.closePolarisSearchDialog();
    await flushDialogCloseEvents();
    editId.value = '9007199254740996';
    document.getElementById('edit-title').value = 'Different request B';
    setLinkedUrl('9007199254740996');
    document.getElementById('edit-title').focus();
    const newerUrl = window.location.href;
    await finishAction(differentEditAction);
    assert.equal(editModal.open, true);
    assert.equal(editId.value, '9007199254740996');
    assert.equal(document.getElementById('edit-title').value, 'Different request B');
    assert.equal(window.location.href, newerUrl, 'stale A cannot clear B deep link');
    assert.equal(editRefreshModes.at(-1), 'silent');
    assert.equal(document.activeElement, document.getElementById('edit-title'), 'stale A cannot steal B focus');
    editModal.close();
    await flushDialogCloseEvents();

    // A replacement session with the same staff ID still invalidates a pending action's UI ownership.
    const oldSessionAction = await confirmPendingAction(await openReadyEdit('Old session pending'));
    state.setStaffSession({ authenticated: false, antiforgeryToken: 'test-token' });
    closeOpenDialogs();
    await flushDialogCloseEvents();
    assert.equal(editModal.open, false, 'access teardown cannot reopen the edit form');
    state.setStaffSession({
      authenticated: true, accessAllowed: true, antiforgeryToken: 'new-session-token',
      staff: { id: '7', role: 'super_admin', organizationId: 1, userPrincipalName: 'staff@example.org' }
    });
    editModal.showModal();
    editId.value = '9007199254740996';
    const refreshesBeforeSessionCompletion = editRefreshes;
    await finishAction(oldSessionAction);
    assert.equal(editModal.open, true);
    assert.equal(editId.value, '9007199254740996');
    assert.equal(editRefreshes, refreshesBeforeSessionCompletion);
    editModal.close();
    await flushDialogCloseEvents();

    // A late access failure from the old request cannot sign out the replacement session.
    const oldUnauthorizedAction = await confirmPendingAction(await openReadyEdit('Old unauthorized action'));
    state.setStaffSession({ authenticated: false, antiforgeryToken: 'new-session-token' });
    closeOpenDialogs();
    await flushDialogCloseEvents();
    state.setStaffSession({
      authenticated: true, accessAllowed: true, antiforgeryToken: 'replacement-token',
      staff: { id: '7', role: 'super_admin', organizationId: 1, userPrincipalName: 'staff@example.org' }
    });
    oldUnauthorizedAction.pending.resolve(response(401, { code: 'staff_session_invalid', message: 'Old session ended.' }));
    await settle();
    assert.equal(state.staffSession.authenticated, true);
    assert.equal(state.staffSession.antiforgeryToken, 'replacement-token');
    assert.equal(dialog.open, false);
    assert.equal(editModal.open, false);

    // A duplicate response from an old interaction cannot prompt over a newer edit request.
    state.setCurrentSuggestions([title, copy]);
    const staleDuplicate = await confirmPendingAction(await openReadyEdit('Stale duplicate action'));
    search.closePolarisSearchDialog();
    await flushDialogCloseEvents();
    editId.value = '9007199254740996';
    setLinkedUrl('9007199254740996');
    document.getElementById('edit-title').focus();
    const duplicateNewerUrl = window.location.href;
    staleDuplicate.pending.resolve(response(409, {
      code: 'duplicate_open_request', message: 'Another request has this BIB.'
    }));
    await settle();
    assert.equal(document.getElementById('confirm-dialog').open, false);
    assert.equal(document.getElementById('alert-dialog').open, false);
    assert.equal(editModal.open, true);
    assert.equal(window.location.href, duplicateNewerUrl);
    assert.equal(document.activeElement, document.getElementById('edit-title'));
    assert.equal(editRefreshModes.at(-1), 'silent');
    editModal.close();
    await flushDialogCloseEvents();

    // The current duplicate confirmation closes using the existing title-request version.
    setLinkedUrl(id);
    const ownedDuplicate = await confirmPendingAction(await openReadyEdit('Owned duplicate action'));
    ownedDuplicate.pending.resolve(response(409, {
      code: 'duplicate_open_request', message: 'Another request has this BIB.'
    }));
    await settle();
    assert.equal(document.getElementById('confirm-dialog').open, true);
    document.getElementById('confirm-dialog-ok').click();
    await settle();
    await flushDialogCloseEvents();
    assert.equal(actions.at(-1).body.action, 'closeDuplicate');
    assert.ok(actions.at(-1).body.version, 'duplicate close sends the current RowVersion');
    assert.equal(document.getElementById('confirm-dialog').open, false, 'one confirmation is sufficient');
    assert.equal(dialog.open, false);
    assert.equal(editModal.open, false);
    assert.equal(window.location.search, '?stage=submitted');
    assert.equal(editRefreshModes.at(-1), 'normal');

    // Model checkAuth's queued close event after 401 while edit-origin Polaris is open.
    const accessLostButton = await openReadyEdit('Access lost while open');
    accessLostButton.click();
    await settle();
    const accessLostConfirmation = document.getElementById('confirm-additional-copy-reminder')?.closest('dialog');
    assert.ok(accessLostConfirmation?.open);
    state.setStaffSession({ authenticated: false, antiforgeryToken: 'test-token', code: 'staff_session_invalid' });
    closeOpenDialogs();
    await flushDialogCloseEvents();
    assert.equal(dialog.open, false);
    assert.equal(document.body.contains(accessLostConfirmation), false,
      'access teardown removes a pending confirmation without dispatching its action');
    assert.equal(editModal.open, false, 'a queued close event cannot resurrect the parent after 401');
    state.setStaffSession({
      authenticated: true, accessAllowed: true, antiforgeryToken: 'next-session-token',
      staff: { id: '7', role: 'super_admin', organizationId: 1, userPrincipalName: 'staff@example.org' }
    });
    await flushDialogCloseEvents();
    assert.equal(editModal.open, false, 'a later same-staff session inherits no stale edit dialog');

    // The real legacy refresh path restores focus for an ordinary row claim.
    const grid = await import(pathToFileURL(path.join(temporary, 'staff/js/grid.js')).href);
    window.history.replaceState(null, '', '/staff/?stage=submitted');
    state.setCurrentSuggestions([title]);
    state.setAllSuggestions([title]);
    const detachedClaimButton = document.createElement('button');
    document.getElementById('grid-container').appendChild(detachedClaimButton);
    detachedClaimButton.focus();
    await grid.claimRequest({ type: 'title_request', id });
    assert.equal(detachedClaimButton.isConnected, false, 'the grid refresh removes the old row control');
    assert.equal(document.activeElement, document.getElementById('tab-desc'),
      'ordinary row action refresh gives focus to the workflow header');

    // Silent refresh may update grid data without disturbing a newer edit control or deep link.
    setLinkedUrl('9007199254740996');
    editId.value = '9007199254740996';
    editModal.showModal();
    document.getElementById('edit-title').focus();
    const silentUrl = window.location.href;
    await grid.refreshCurrentStaffView({ silent: true });
    assert.equal(window.location.href, silentUrl);
    assert.equal(editModal.open, true);
    assert.equal(document.activeElement, document.getElementById('edit-title'));
    editModal.close();
    state.setCurrentStatus('settings');
    editModal.showModal();
    document.getElementById('edit-title').focus();
    await grid.refreshCurrentStaffView({ silent: true });
    assert.equal(editModal.open, true, 'a stale workflow completion cannot run Settings dialog teardown');
    assert.equal(document.activeElement, document.getElementById('edit-title'));
    editModal.close();
    await runMultiResultChecks();
  } finally {
    dom?.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
  console.log('Staff Polaris identity and race tests passed.');
})().catch(error => { console.error(error); process.exitCode = 1; });
