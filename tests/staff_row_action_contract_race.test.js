const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

const response = (status, body) => ({ ok: status < 400, status,
  statusText: status < 400 ? 'OK' : 'Request failed', json: async () => body });
function deferred() {
  let resolve;
  const promise = new Promise(yes => { resolve = yes; });
  return { promise, resolve };
}
async function settle() {
  for (let index = 0; index < 6; index += 1) await new Promise(resolve => setImmediate(resolve));
}

(async () => {
  const root = path.resolve(__dirname, '..');
  const frontend = process.env.ASAP_FRONTEND_TEST_ROOT || path.join(root, 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-row-actions-'));
  let dom;
  try {
    fs.cpSync(path.join(frontend, 'staff'), path.join(temporary, 'staff'), { recursive: true });
    fs.cpSync(path.join(frontend, 'shared'), path.join(temporary, 'shared'), { recursive: true });
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
    dom = new JSDOM(fs.readFileSync(path.join(frontend, 'staff/index.html'), 'utf8'), {
      url: 'https://localhost/staff/'
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.CustomEvent = dom.window.CustomEvent;
    global.Event = dom.window.Event;
    global.FormData = dom.window.FormData;
    global.Node = dom.window.Node;
    global.localStorage = dom.window.localStorage;
    global.navigator = dom.window.navigator;
    global.requestAnimationFrame = callback => callback();
    dom.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    dom.window.HTMLDialogElement.prototype.close = function () {
      this.open = false;
      this.dispatchEvent(new dom.window.Event('close'));
    };

    const candidates = [];
    const previews = [];
    const pickupLoads = [];
    const pickupSaves = [];
    const posts = [];
    let failNextCreate = false;
    let deferPickup = false;
    global.fetch = (url, options = {}) => {
      const endpoint = String(url);
      if (endpoint.includes('/assignment-candidates?')) {
        const pending = deferred();
        candidates.push(pending);
        return pending.promise;
      }
      if (endpoint.endsWith('/additional-copy') && (options.method || 'GET') === 'GET') {
        const pending = deferred();
        previews.push(pending);
        return pending.promise;
      }
      if (options.method === 'POST') {
        posts.push({ endpoint, body: JSON.parse(options.body) });
        if (endpoint.endsWith('/pickup-options')) {
          if (deferPickup) {
            const pending = deferred();
            pickupLoads.push(pending);
            return pending.promise;
          }
          return Promise.resolve(response(200, {}));
        }
        if (endpoint.endsWith('/pickup-preference')) {
          const pending = deferred();
          pickupSaves.push(pending);
          return pending.promise;
        }
        if (endpoint.endsWith('/additional-copy') && failNextCreate) {
          failNextCreate = false;
          return Promise.resolve(response(409, { code: 'stale_version', message: 'Request changed.' }));
        }
        return Promise.resolve(response(200, { request: { type: 'title_request', id: '1',
          status: posts.at(-1).body.status || 'suggestion', version: `updated-${posts.length}` } }));
      }
      if (endpoint.includes('/title-requests?') || endpoint.includes('/additional-copies?')) {
        return Promise.resolve(response(200, { items: [], scope: 'all' }));
      }
      throw new Error(`Unexpected request ${endpoint}`);
    };
    const state = await import(pathToFileURL(path.join(temporary, 'staff/js/state.js')).href);
    const actions = await import(pathToFileURL(path.join(temporary, 'staff/js/grid-actions.js')).href);
    const modals = await import(pathToFileURL(path.join(temporary, 'staff/js/modals.js')).href);
    const edit = await import(pathToFileURL(path.join(temporary, 'staff/js/modals/edit-submit.js')).href);
    const dialogs = await import(pathToFileURL(path.join(temporary, 'staff/js/dialogs.js')).href);
    const modalContext = await import(pathToFileURL(path.join(temporary, 'staff/js/modals/context.js')).href);
    const staff = id => ({ authenticated: true, accessAllowed: true, antiforgeryToken: 'test-token',
      staff: { id, role: 'super_admin', organizationId: 1, userPrincipalName: 'admin@example.org' } });
    state.setStaffSession(staff('7'));
    const title = (id, status, bibid = '') => ({ type: 'title_request', id, status,
      title: `Title ${id}`, libraryOrgId: '2', version: `version-${id}`, bibid, format: 'book', autohold: true });
    const copy = (id, status) => ({ type: 'additional_copy', id, status,
      title: `Copy ${id}`, libraryOrgId: '2', version: `copy-version-${id}`, bibid: '9003' });
    const rows = [title('1', 'suggestion'), title('2', 'outstanding_purchase', '9003'),
      title('3', 'pending_hold', '9003'), title('4', 'hold_placed', '9003'),
      title('5', 'closed'), copy('5', 'closed'), copy('6', 'open')];
    state.setCurrentSuggestions(rows);
    state.setAllSuggestions(rows);
    const ctx = { staffSession: state.staffSession, get currentStatus() { return state.currentStatus; },
      get currentSuggestions() { return state.currentSuggestions; },
      get allSuggestions() { return state.allSuggestions; } };
    const allActions = row => {
      state.setCurrentStatus(row.type === 'additional_copy' && row.status === 'open'
        ? 'additional_copies' : row.status);
      const group = actions.getRowActions(row, ctx);
      return [group.primary, ...(group.visible || []), ...group.secondary].filter(Boolean);
    };
    const action = (row, key) => {
      const found = allActions(row).find(item => item.key === key);
      assert.ok(found, `${row.type}/${row.status} exposes ${key}`);
      return found;
    };
    const modal = document.getElementById('editModal');
    const obsoletePurchase = action(rows[0], 'purchase');
    state.setCurrentSuggestions(rows.filter(row => row !== rows[0]));
    await obsoletePurchase.onClick();
    assert.equal(modal.open, false, 'removed rows cannot launch an old action');
    state.setCurrentSuggestions([{ ...rows[0], status: 'closed', version: 'new-version' }, ...rows.slice(1)]);
    await obsoletePurchase.onClick();
    assert.equal(modal.open, false, 'changed rows cannot launch an old transition');
    state.setCurrentSuggestions(rows);
    const checkModal = async (row, key, expectedAction, expectedStatus) => {
      await action(row, key).onClick();
      assert.equal(document.getElementById('edit-action').value, expectedAction, key);
      assert.equal(document.getElementById('edit-next-status').value, expectedStatus, key);
      assert.equal(document.getElementById('edit-id').dataset.requestType, 'title_request');
      modal.close();
    };
    for (const [row, key, backendAction, next] of [
      [rows[0], 'purchase', 'purchase', 'outstanding_purchase'],
      [rows[0], 'reject', 'reject', 'closed'],
      [rows[0], 'alreadyOwn', 'alreadyOwn', 'pending_hold'],
      [rows[0], 'silentClose', 'silentClose', 'closed'],
      [rows[0], 'edit', 'edit', 'suggestion'],
      [rows[1], 'queueHold', 'catalogFound', 'pending_hold'],
      [rows[1], 'edit', 'edit', 'outstanding_purchase'],
      [rows[2], 'edit', 'edit', 'pending_hold'],
      [rows[3], 'close', 'close', 'closed'],
      [rows[3], 'edit', 'edit', 'hold_placed'],
      [rows[4], 'edit', 'edit', 'closed']
    ]) await checkModal(row, key, backendAction, next);
    assert.equal(allActions(rows[1]).some(item => ['undo', 'silentClose'].includes(item.key)), false);
    assert.equal(allActions(rows[2]).some(item => ['undo', 'silentClose'].includes(item.key)), false);
    assert.equal(allActions(rows[3]).some(item => ['undo', 'silentClose'].includes(item.key)), false);
    assert.ok(allActions(rows[2]).some(item => item.key === 'buyAnotherCopy'));
    assert.ok(allActions(rows[3]).some(item => item.key === 'buyAnotherCopy'));
    assert.ok(allActions(rows[4]).some(item => item.key === 'undo'));
    assert.ok(allActions(rows[5]).some(item => item.key === 'undo'));
    assert.ok(allActions(rows[6]).some(item => item.key === 'closeAdditionalCopy'));
    assert.equal(allActions(rows[4]).some(item => ['claim', 'unclaim', 'clearClaim'].includes(item.key)), false);
    assert.equal(allActions(rows[5]).some(item => ['claim', 'unclaim', 'clearClaim'].includes(item.key)), false);
    state.setCurrentStatus('additional_copies');
    assert.equal(actions.getRowActions(rows[5], ctx).primary.key, 'undo',
      'the closed additional-copy filter must expose Reopen rather than Close');
    modals.openEdit(rows[6], 'open', 'Additional-copy task', '', 'Save');
    assert.equal(document.getElementById('edit-submit-btn').hidden, true,
      'additional-copy detail cannot expose a Save action that has no endpoint');
    document.getElementById('editModal').close();
    modals.openEdit(rows[0], 'suggestion', 'Edit suggestion', 'edit', 'Save');
    assert.equal(document.getElementById('edit-submit-btn').hidden, false);
    document.getElementById('editModal').close();

    await action(rows[0], 'edit').onClick();
    document.getElementById('edit-title').value = 'Edited via row action';
    await edit.submitEditForm({ preventDefault() {} }, modalContext.createModalContext(state), { onRefresh: async () => {} });
    assert.equal(posts.at(-1).endpoint, '/api/asap/staff/title-requests/1/action');
    assert.equal(posts.at(-1).body.action, 'edit');
    assert.equal(posts.at(-1).body.status, 'suggestion');
    assert.equal(posts.at(-1).body.exactPublicationDate, null,
      'an empty date must bind to nullable DateOnly on the real endpoint');
    const submitModalAction = async (row, key, bibid) => {
      await action(row, key).onClick();
      if (bibid !== undefined) {
        document.getElementById('edit-bibid').value = bibid;
        state.setVerifiedBibId(bibid);
      }
      await edit.submitEditForm({ preventDefault() {} }, modalContext.createModalContext(state),
        { onRefresh: async () => {} });
      return posts.at(-1).body;
    };
    const purchased = await submitModalAction(rows[0], 'purchase', '9003');
    assert.equal(purchased.action, 'purchase');
    assert.equal(purchased.status, 'pending_hold', 'Purchase must use the submitted BIB');
    assert.equal(purchased.version, rows[0].version);
    const queued = await submitModalAction(rows[1], 'queueHold', '9003');
    assert.equal(queued.action, 'catalogFound');
    assert.equal(queued.status, 'pending_hold');
    assert.equal(queued.bibid, '9003');
    const closed = await submitModalAction(rows[3], 'close');
    assert.equal(closed.action, 'close');
    assert.equal(closed.status, 'closed');

    const abandonedConfirm = dialogs.showConfirm('Old confirmation');
    dialogs.closeOpenDialogs();
    assert.equal(await abandonedConfirm, false);
    const replacementConfirm = dialogs.showConfirm('Replacement confirmation');
    document.getElementById('confirm-dialog-ok').click();
    assert.equal(await replacementConfirm, true);

    const closedUndo = action(rows[4], 'undo').onClick();
    document.getElementById('confirm-dialog-ok').click();
    await closedUndo;
    assert.equal(posts.at(-1).endpoint, '/api/asap/staff/title-requests/5/action');
    assert.equal(posts.at(-1).body.action, 'reopen');
    assert.equal(posts.at(-1).body.status, 'suggestion');
    const copyUndo = action(rows[5], 'undo').onClick();
    document.getElementById('confirm-dialog-ok').click();
    await copyUndo;
    assert.equal(posts.at(-1).endpoint, '/api/asap/staff/additional-copies/5/reopen');
    await settle();
    state.setCurrentSuggestions(rows);
    state.setAllSuggestions(rows);

    const assignA = title('10', 'suggestion');
    const assignB = title('11', 'suggestion');
    state.setCurrentStatus('suggestion');
    state.setCurrentSuggestions([assignA, assignB]);
    state.setAllSuggestions([assignA, assignB]);
    const first = actions.openAssignDialog(assignA);
    const second = actions.openAssignDialog(assignB);
    candidates.at(-1).resolve(response(200, { candidates: [{ id: '77', displayName: 'B staff' }] }));
    await second;
    candidates.at(-2).resolve(response(200, { candidates: [{ id: '88', displayName: 'A staff' }] }));
    await first;
    const assignDialog = document.getElementById('assign-dialog');
    const select = document.getElementById('assign-staff-select');
    assert.equal(assignDialog.open, true);
    assert.match(document.getElementById('assign-dialog-context').textContent, /Title 11/);
    assert.match(select.textContent, /B staff/);
    assert.doesNotMatch(select.textContent, /A staff/);
    select.value = '77';
    select.onchange();
    document.getElementById('assign-confirm').click();
    await settle();
    assert.equal(posts.at(-1).endpoint, '/api/asap/staff/title-requests/11/assign');
    assert.equal(posts.at(-1).body.version, assignB.version);

    const staleA = actions.openAssignDialog(assignA);
    const latestB = actions.openAssignDialog(assignB);
    candidates.at(-2).resolve(response(200, { candidates: [{ id: '88', displayName: 'A staff' }] }));
    await staleA;
    assert.equal(assignDialog.open, false);
    candidates.at(-1).resolve(response(200, { candidates: [{ id: '77', displayName: 'B staff' }] }));
    await latestB;
    const oldConfirm = document.getElementById('assign-confirm').onclick;
    const collidingCopy = copy('11', 'open');
    state.setCurrentSuggestions([assignB, collidingCopy]);
    state.setAllSuggestions([assignB, collidingCopy]);
    const copyAssign = actions.openAssignDialog(collidingCopy);
    candidates.at(-1).resolve(response(200, { candidates: [{ id: '79', displayName: 'Copy staff' }] }));
    await copyAssign;
    const postCount = posts.length;
    await oldConfirm();
    assert.equal(posts.length, postCount, 'replaced Confirm cannot mutate the old title');
    select.value = '79';
    select.onchange();
    document.getElementById('assign-confirm').click();
    await settle();
    assert.equal(posts.at(-1).endpoint, '/api/asap/staff/additional-copies/11/assign');

    const pendingSessionAssign = actions.openAssignDialog(assignB);
    state.setStaffSession(staff('8'));
    candidates.at(-1).resolve(response(200, { candidates: [{ id: '77', displayName: 'Old staff' }] }));
    await pendingSessionAssign;
    assert.equal(assignDialog.open, false);
    state.setStaffSession(staff('7'));

    const source = title('20', 'pending_hold', '9003');
    const newer = title('21', 'pending_hold', '9003');
    state.setCurrentStatus('pending_hold');
    state.setCurrentSuggestions([source, newer, copy('20', 'open')]);
    state.setAllSuggestions(state.currentSuggestions);
    const oldPreview = actions.buyAnotherCopyForRow(source);
    state.setStaffSession(staff('8'));
    previews.at(-1).resolve(response(200, { bibid: '9003', openCount: 0 }));
    await oldPreview;
    assert.equal(document.querySelector('dialog.asap-dialog-small:not([id])'), null);
    state.setStaffSession(staff('7'));
    const replacedPreview = actions.buyAnotherCopyForRow(source);
    await action(newer, 'edit').onClick();
    previews.at(-1).resolve(response(200, { bibid: '9003', openCount: 0 }));
    await replacedPreview;
    assert.equal(document.querySelector('dialog.asap-dialog-small:not([id])'), null);
    modal.close();
    const beforeCollision = previews.length;
    await actions.buyAnotherCopyForRow(copy('20', 'open'));
    assert.equal(previews.length, beforeCollision);
    const openConfirmation = actions.buyAnotherCopyForRow(source);
    previews.at(-1).resolve(response(200, { bibid: '9003', openCount: 0 }));
    await settle();
    const confirmation = document.querySelector('dialog.asap-dialog-small:not([id])');
    assert.ok(confirmation?.open);
    state.setStaffSession(staff('8'));
    [...confirmation.querySelectorAll('button')].find(button => button.textContent === 'Confirm').click();
    await openConfirmation;
    assert.equal(posts.filter(item => item.endpoint.endsWith('/additional-copy')).length, 0);
    state.setStaffSession(staff('7'));
    const oldFocus = document.createElement('button');
    document.body.append(oldFocus);
    oldFocus.focus();
    const changedConfirmation = actions.buyAnotherCopyForRow(source);
    previews.at(-1).resolve(response(200, { bibid: '9003', openCount: 0 }));
    await settle();
    const changedDialog = document.querySelector('dialog.asap-dialog-small:not([id])');
    assert.ok(changedDialog?.open);
    state.setCurrentSuggestions([{ ...source, version: 'changed-version' }, newer]);
    [...changedDialog.querySelectorAll('button')].find(button => button.textContent === 'Confirm').click();
    await changedConfirmation;
    assert.equal(posts.filter(item => item.endpoint.endsWith('/additional-copy')).length, 0);
    assert.notEqual(document.activeElement, oldFocus, 'stale confirmation cannot restore old row focus');
    oldFocus.remove();
    state.setCurrentSuggestions([source, newer, copy('20', 'open')]);
    const staleVersionPreview = actions.buyAnotherCopyForRow(source);
    previews.at(-1).resolve(response(200, { bibid: '9003', openCount: 0 }));
    await settle();
    const staleVersionConfirm = document.querySelector('dialog.asap-dialog-small:not([id])');
    assert.ok(staleVersionConfirm?.open);
    const successToasts = [...document.querySelectorAll('.asap-toast-success')].length;
    failNextCreate = true;
    [...staleVersionConfirm.querySelectorAll('button')].find(button => button.textContent === 'Confirm').click();
    await assert.rejects(staleVersionPreview, /Request changed/);
    assert.equal(posts.at(-1).body.version, source.version);
    assert.equal([...document.querySelectorAll('.asap-toast-success')].length, successToasts);

    // Same-request reopen and different-request replacement cannot accept old pickup responses.
    deferPickup = true;
    state.setCurrentStatus('pending_hold');
    state.setCurrentSuggestions([source, newer]);
    state.setAllSuggestions([source, newer]);
    modals.openEdit(source, 'pending_hold', 'Edit', 'edit', 'Save');
    const oldPickup = pickupLoads.at(-1);
    modal.close();
    modals.openEdit(source, 'pending_hold', 'Edit', 'edit', 'Save');
    const newPickup = pickupLoads.at(-1);
    newPickup.resolve(response(200, { requestId: source.id,
      pickupBranches: [{ id: '101', label: 'New Branch' }] }));
    await settle();
    oldPickup.resolve(response(200, { requestId: source.id,
      pickupBranches: [{ id: '102', label: 'Old Branch' }] }));
    await settle();
    const pickupSelect = document.getElementById('edit-pickup-branch');
    assert.match(pickupSelect.textContent, /New Branch/);
    assert.doesNotMatch(pickupSelect.textContent, /Old Branch/);
    pickupSelect.value = '101';
    pickupSelect.dispatchEvent(new dom.window.Event('change'));
    document.getElementById('edit-pickup-save-btn').click();
    await settle();
    assert.equal(pickupSaves.length, 1);
    modal.close();
    modals.openEdit(newer, 'pending_hold', 'Edit', 'edit', 'Save');
    pickupLoads.at(-1).resolve(response(200, { requestId: newer.id,
      pickupBranches: [{ id: '103', label: 'B Branch' }] }));
    await settle();
    const pickupToasts = [...document.querySelectorAll('.asap-toast-success')].length;
    pickupSaves[0].resolve(response(200, { request: { ...source, version: 'saved-source-version' },
      pickupChanged: true }));
    await settle();
    assert.match(pickupSelect.textContent, /B Branch/);
    assert.equal([...document.querySelectorAll('.asap-toast-success')].length, pickupToasts);
    modal.close();
    console.log('Staff row action contracts and ownership races passed.');
  } finally {
    dom?.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
