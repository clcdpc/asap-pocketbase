const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

const frontend = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend');
const response = (status, body) => ({
  ok: status < 400,
  status,
  statusText: status < 400 ? 'OK' : 'Request failed',
  json: async () => body
});
async function until(predicate, message) {
  const deadline = Date.now() + 3000;
  while (!predicate() && Date.now() < deadline) await new Promise(resolve => setTimeout(resolve, 10));
  assert.ok(predicate(), message);
}

async function scenario(action, profileDefault, explicitChoice, options = {}) {
  const scenarioOptions = options;
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-request-actions-'));
  let dom;
  try {
    fs.cpSync(path.join(frontend, 'staff'), path.join(temporary, 'staff'), { recursive: true });
    fs.cpSync(path.join(frontend, 'shared'), path.join(temporary, 'shared'), { recursive: true });
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
    const id = '9007199254740993';
    const otherId = '9007199254740994';
    const templateId = '9007199254740995';
    dom = new JSDOM(fs.readFileSync(path.join(frontend, 'staff', 'index.html'), 'utf8'), {
      url: `https://localhost/staff/?request=${id}`,
      pretendToBeVisual: true
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.Node = dom.window.Node;
    global.HTMLElement = dom.window.HTMLElement;
    global.DOMParser = dom.window.DOMParser;
    global.FormData = dom.window.FormData;
    global.URLSearchParams = dom.window.URLSearchParams;
    dom.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    dom.window.HTMLDialogElement.prototype.close = function () { this.open = false; };
    dom.window.gridjs = require(path.join(frontend, 'vendor/gridjs/6.2.0/gridjs.umd.js'));
    const staff = {
      id: '20', role: 'staff', organizationId: 2, organizationName: 'Library',
      displayName: 'Staff', userPrincipalName: 'staff@example.org',
      purchaseReminderDefault: profileDefault, defaultMineUnclaimedFilter: false
    };
    const configuration = {
      availableFormats: ['book'], formatLabels: { book: 'Book' }, publicationOptions: [],
      additionalFieldDefinitions: [{ key: 'shelf', label: 'Shelf', type: 'text' }],
      formatRules: { book: { customFields: { shelf: { mode: 'optional' } } } }
    };
    const request = {
      id, version: 'v1', type: 'title_request', title: 'Original title', author: null,
      libraryOrgId: 2, libraryOrgName: 'Library', barcode: '20000000000001',
      status: 'suggestion', format: 'book', formatLabel: 'Book', identifier: null,
      bibid: scenarioOptions.verifyBib || scenarioOptions.verificationInvalidation ? '9001' : null,
      bibidStaffVerified: Boolean(scenarioOptions.verificationInvalidation),
      claimedByStaffUserId: scenarioOptions.unclaimedPreview ? null : staff.id,
      publication: null, exactPublicationDate: '2026-01-02',
      notes: 'Editable note', autohold: false,
      customFields: { shelf: { value: 'Reference', type: 'text', label: 'Shelf' } },
      workflowTags: [], phaseEnteredAt: '2026-01-01T00:00:00Z',
      created: '2026-01-01T00:00:00Z', updated: '2026-01-01T00:00:00Z',
      capabilities: { canEditIdentifier: true, canChangeBib: true, canChangeWorkflowState: true },
      activity: [
        { id: '9007199254740997', eventType: 'status_changed', actorType: 'staff',
          actorName: null, message: '<svg onload=alert(1)>', created: '2026-01-01T00:00:00Z' },
        { id: '9007199254740998', eventType: 'staff_note', actorType: 'staff',
          actorName: 'Named <img src=x onerror=alert(1)>', message: 'Second', created: '2026-01-01T00:00:00Z' }
      ]
    };
    let currentRequest = request;
    const otherRequest = { ...request, id: otherId, title: 'Other request', activity: [] };
    let payload = null;
    let committed = false;
    let postCommitQueueLoads = 0;
    let releaseTemplate = null;
    let releaseMutation = null;
    let releaseFollowupQueue = null;
    global.fetch = async (url, options = {}) => {
      if (url.endsWith('/session')) {
        return response(200, { authenticated: true, accessAllowed: true, antiforgeryToken: 'token', staff });
      }
      if (url.includes('/title-requests?')) {
        if (committed && (scenarioOptions.laterUnrelated401 || scenarioOptions.laterUnrelated403)) {
          postCommitQueueLoads += 1;
          if (postCommitQueueLoads > 1) {
            return scenarioOptions.laterUnrelated403
              ? response(403, { code: 'staff_scope_forbidden', accessAllowed: false })
              : response(401, { code: 'staff_session_invalid' });
          }
          if (scenarioOptions.closeDuringFollowup) {
            return new Promise(resolve => { releaseFollowupQueue = resolve; });
          }
        }
        if (committed && scenarioOptions.queue401) {
          return response(401, { code: 'staff_session_invalid' });
        }
        if (committed && scenarioOptions.queue403) {
          return response(403, { code: 'staff_scope_forbidden', accessAllowed: false });
        }
        return response(200, { scope: '2', organizations: [],
          items: scenarioOptions.staleChoice || scenarioOptions.staleMutation
            ? [currentRequest, otherRequest] : [currentRequest] });
      }
      if (url.endsWith(`/title-requests/${id}`)) {
        return committed && scenarioOptions.detailRefreshFails
          ? response(503, { message: 'Detail unavailable' })
          : response(200, currentRequest);
      }
      if (url.endsWith(`/title-requests/${otherId}`)) return response(200, otherRequest);
      if (url.endsWith('/bib-lookup') && options.method === 'POST') {
        return response(200, { bibId: '9001', title: 'Original title', author: null });
      }
      if (url.includes('/api/asap/config?')) return response(200, configuration);
      if (url.includes('/research-configuration')) return response(200, { externalSearchProviders: [] });
      if (url.endsWith(`/title-requests/${id}/rejection-templates`)) {
        if (scenarioOptions.staleChoice) {
          return new Promise(resolve => { releaseTemplate = resolve; });
        }
        return response(200, { items: [{ id: templateId, name: 'Named <img src=x onerror=alert(1)>' }],
          defaultTemplateId: null });
      }
      if (url.endsWith(`/title-requests/${otherId}/rejection-templates`)) {
        return response(200, { items: [{ id: '9007199254740996', name: 'Other template' }],
          defaultTemplateId: null });
      }
      if (url.endsWith(`/title-requests/${id}/action`) && options.method === 'POST') {
        payload = JSON.parse(options.body);
        const finalStatus = action === 'reject' ? 'closed' : 'outstanding_purchase';
        currentRequest = { ...currentRequest, version: 'v2', status: finalStatus,
          activity: [...currentRequest.activity, {
          id: '9007199254740999', eventType: 'status_changed', actorType: 'staff',
          actorName: 'Staff', message: 'Committed', created: '2026-01-02T00:00:00Z'
        }] };
        committed = true;
        const result = response(200, { committed: true,
          request: scenarioOptions.detailRefreshFails ? null : currentRequest, finalStatus,
          notificationStatus: action === 'purchase' && explicitChoice ? 'suppressed' : 'not_requested',
          notificationReason: action === 'purchase' && explicitChoice ? 'mail_not_configured' : null,
          refreshUnavailable: false });
        return scenarioOptions.staleMutation
          ? new Promise(resolve => { releaseMutation = () => resolve(result); })
          : result;
      }
      throw new Error(`Unexpected request ${url}`);
    };
    const workflow = await import(pathToFileURL(path.join(temporary, 'staff/js/workflow.js')).href);
    await workflow.createWorkflowApp().start();
    await until(() => document.querySelector('#request-dialog').open, 'request detail opens');
    const events = document.querySelectorAll('.request-activity li');
    assert.equal(events.length, 2);
    assert.equal(events[0].dataset.eventId, '9007199254740997');
    assert.match(events[0].textContent, /Actor not recorded/);
    assert.equal(document.querySelector('.request-activity svg'), null);
    assert.equal(document.querySelector('.request-activity img'), null);
    assert.equal(document.querySelector('.edit-form textarea').value, 'Editable note');
    if (scenarioOptions.unclaimedPreview) {
      assert.match(document.querySelector('.pending-audit-preview').textContent, /staff claim/);
      assert.equal(document.querySelector('.edit-form button[type="submit"]').disabled, false);
      return;
    }
    assert.match(document.querySelector('.pending-audit-preview').textContent, /No pending changes/);
    assert.equal(document.querySelector('.edit-form button[type="submit"]').disabled, true);
    document.querySelector('.edit-form').dispatchEvent(
      new dom.window.Event('submit', { bubbles: true, cancelable: true }));
    assert.equal(payload, null, 'unchanged edit must not generate a claim event');

    const title = document.querySelector('.edit-form input');
    title.value = 'Changed title';
    title.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    assert.match(document.querySelector('.pending-audit-preview').textContent, /Pending changes \(not saved\): title/);
    title.value = 'Original title';
    title.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    assert.match(document.querySelector('.pending-audit-preview').textContent, /No pending changes/);
    title.value = '  Original title  ';
    title.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    assert.match(document.querySelector('.pending-audit-preview').textContent, /No pending changes/);
    const exactDate = document.querySelector('.edit-form input[type="date"]');
    exactDate.value = '';
    exactDate.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    assert.match(document.querySelector('.pending-audit-preview').textContent, /exact publication date/);
    exactDate.value = '2026-01-02';
    exactDate.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    assert.match(document.querySelector('.pending-audit-preview').textContent, /No pending changes/);

    if (scenarioOptions.verifyBib) {
      [...document.querySelectorAll('.edit-form button')]
        .find(item => item.textContent.includes('Search Polaris catalog')).click();
      await until(() => document.querySelector('#polaris-dialog').open, 'Polaris lookup opens');
      document.querySelector('#polaris-form').dispatchEvent(
        new dom.window.Event('submit', { bubbles: true, cancelable: true }));
      await until(() => document.querySelector('#polaris-results button'), 'BIB result appears');
      document.querySelector('#polaris-results button').click();
      await until(() => /BIB verification/.test(document.querySelector('.pending-audit-preview').textContent),
        'same-BIB Polaris selection previews verification');
      return;
    }
    if (scenarioOptions.verificationInvalidation) {
      const bib = document.querySelector('.edit-form input[inputmode="numeric"]');
      bib.value = '';
      bib.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
      assert.match(document.querySelector('.pending-audit-preview').textContent, /BIB ID, BIB verification/);
      return;
    }

    async function openOtherRequest() {
      document.querySelector('#close-request').click();
      await until(() => !document.querySelector('#request-dialog').open, 'first dialog closes');
      await until(() => document.querySelector(`[aria-label="Open request ${otherId}"]`), 'other row appears');
      document.querySelector(`[aria-label="Open request ${otherId}"]`).click();
      await until(() => document.querySelector('#request-dialog-title').textContent === 'Other request',
        'other detail opens');
    }

    if (scenarioOptions.staleChoice) {
      [...document.querySelectorAll('.action-bar button')].find(item => item.textContent.includes('Reject')).click();
      await until(() => releaseTemplate, 'first template request starts');
      await openOtherRequest();
      [...document.querySelectorAll('.action-bar button')].find(item => item.textContent.includes('Reject')).click();
      await until(() => document.querySelector('.action-choice select').options.length === 2,
        'other template choice loads');
      releaseTemplate(response(200, { items: [{ id: templateId, name: 'Stale template' }],
        defaultTemplateId: templateId }));
      await new Promise(resolve => setImmediate(resolve));
      const values = [...document.querySelector('.action-choice select').options].map(item => item.value);
      assert.deepEqual(values, ['', '9007199254740996']);
      assert.equal(payload, null, 'stale Reject choice must not submit');
      return;
    }

    const button = [...document.querySelectorAll('.action-bar button')]
      .find(item => item.textContent.includes(action === 'reject' ? 'Reject' : 'Purchase'));
    button.click();
    await until(() => document.querySelector('.action-choice'), 'action choice opens');
    let choice = document.querySelector('.action-choice');
    if (action === 'purchase' && profileDefault && !scenarioOptions.staleMutation) {
      document.querySelector('#request-dialog').dispatchEvent(
        new dom.window.Event('cancel', { bubbles: true, cancelable: true }));
      assert.equal(document.querySelector('.action-choice'), null, 'Escape dismisses action choices');
      assert.equal(document.activeElement, button, 'Escape returns focus to action button');
      button.click();
      choice = document.querySelector('.action-choice');
    }
    if (action === 'reject') {
      await until(() => !choice.querySelector('button[type="submit"]').disabled, 'templates load');
      const select = choice.querySelector('select');
      assert.equal(select.options[1].value, templateId);
      assert.equal(choice.querySelector('img'), null);
      select.value = templateId;
    } else {
      const checkbox = choice.querySelector('input[type="checkbox"]');
      assert.equal(checkbox.checked, profileDefault);
      checkbox.checked = explicitChoice;
    }
    choice.dispatchEvent(new dom.window.Event('submit', { bubbles: true, cancelable: true }));
    await until(() => payload !== null, 'action submitted');
    if (action === 'reject') assert.equal(payload.rejectionTemplateId, templateId);
    else assert.equal(payload.emailPurchaseReminder, explicitChoice);
    if (scenarioOptions.staleMutation) {
      await until(() => releaseMutation, 'purchase mutation starts');
      await openOtherRequest();
      releaseMutation();
      await new Promise(resolve => setImmediate(resolve));
      assert.equal(document.querySelector('#request-dialog-title').textContent, 'Other request');
      assert.equal(document.querySelector('#request-dialog .status-badge').textContent, 'Suggestion');
      return;
    }
    if (scenarioOptions.queue401 || scenarioOptions.queue403) {
      await until(() => !document.querySelector('#signed-out').hidden, 'session transition displays result');
      assert.match(document.querySelector('#signed-out-message').textContent, /Final state: Outstanding purchase/);
      assert.match(document.querySelector('#signed-out-message').textContent, /Notification suppressed/);
    } else if (scenarioOptions.laterUnrelated401 || scenarioOptions.laterUnrelated403) {
      await until(() => postCommitQueueLoads === 1, 'committed follow-up queue load starts');
      if (scenarioOptions.closeDuringFollowup) {
        await until(() => releaseFollowupQueue, 'delayed follow-up queue load starts');
        document.querySelector('#close-request').click();
        releaseFollowupQueue(response(200, { scope: '2', organizations: [], items: [currentRequest] }));
      }
      await until(() => !document.querySelector('#refresh-queue').disabled,
        'committed follow-up queue load completes');
      document.querySelector('#refresh-queue').click();
      await until(() => !document.querySelector('#signed-out').hidden,
        'later unrelated session failure displays sign-out');
      assert.match(document.querySelector('#signed-out-message').textContent,
        /session ended|access is not currently available/);
      assert.doesNotMatch(document.querySelector('#signed-out-message').textContent, /Final state:/);
    } else if (scenarioOptions.detailRefreshFails) {
      await until(() => /Details and activity could not refresh/.test(document.querySelector('#app-status').textContent),
        'committed outcome survives detail refresh failure');
      assert.match(document.querySelector('#request-dialog-body').textContent, /The action committed/);
    } else {
      await until(() => document.querySelectorAll('.request-activity li').length === 3,
        'committed activity renders once');
      assert.match(document.querySelector('#app-status').textContent, /Final state:/);
      assert.equal(document.querySelector('.request-activity [data-event-id="9007199254740999"]') !== null, true);
    }
  } finally {
    dom?.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
}

(async () => {
  await scenario('purchase', true, false);
  await scenario('purchase', false, true);
  await scenario('reject', false, true);
  await scenario('purchase', false, true, { queue401: true });
  await scenario('purchase', false, true, { queue403: true });
  await scenario('purchase', false, true, { laterUnrelated401: true });
  await scenario('purchase', false, true, { laterUnrelated401: true, closeDuringFollowup: true });
  await scenario('purchase', false, true, { laterUnrelated403: true });
  await scenario('purchase', false, false, { detailRefreshFails: true });
  await scenario('reject', false, false, { staleChoice: true });
  await scenario('purchase', false, true, { staleMutation: true });
  await scenario('reject', false, false, { verifyBib: true });
  await scenario('reject', false, false, { verificationInvalidation: true });
  await scenario('reject', false, false, { unclaimedPreview: true });
  console.log('Staff request action choice, preview, activity, safe text, and exact ID UI checks passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
