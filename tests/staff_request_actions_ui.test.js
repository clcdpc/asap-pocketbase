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
    const confirmMessages = [];
    dom.window.confirm = message => {
      confirmMessages.push(message);
      return !(scenarioOptions.formatWarning || scenarioOptions.autoClaimOther ||
        scenarioOptions.autoHoldOffBib || scenarioOptions.autoHoldOffOutstandingPurchase ||
        scenarioOptions.autoHoldOnlyOutstandingPurchase) ||
        confirmMessages.length > 1;
    };
    dom.window.gridjs = require(path.join(frontend, 'vendor/gridjs/6.2.0/gridjs.umd.js'));
    const staff = {
      id: '20', role: scenarioOptions.supersededFollowup ? 'super_admin' : 'staff',
      organizationId: 2, organizationName: 'Library',
      displayName: 'Staff', userPrincipalName: 'staff@example.org',
      purchaseReminderDefault: profileDefault, defaultMineUnclaimedFilter: false
    };
    const configuration = {
      availableFormats: scenarioOptions.formatWarning || scenarioOptions.manualFormat
        ? ['book', 'ebook'] : ['book'],
      formatLabels: { book: 'Book', ebook: 'eBook' }, publicationOptions: [],
      additionalFieldDefinitions: [{ key: 'shelf', label: 'Shelf', type: 'text' }],
      formatRules: { book: { customFields: { shelf: { mode: 'optional' } } } }
    };
    const request = {
      id, version: 'v1', type: 'title_request', title: 'Original title', author: null,
      libraryOrgId: 2, libraryOrgName: 'Library', barcode: '20000000000001',
      status: scenarioOptions.autoHoldOffOutstandingPurchase || scenarioOptions.autoHoldOnlyOutstandingPurchase
        ? 'outstanding_purchase'
        : scenarioOptions.pickupWrappedResponse || scenarioOptions.stalePickup ||
          scenarioOptions.unverifiedPendingHold || scenarioOptions.verifiedPendingHold ? 'pending_hold' : 'suggestion',
      format: 'book', formatLabel: 'Book', identifier: null,
      bibid: scenarioOptions.verifyBib || scenarioOptions.verifiedUnchangedBib || scenarioOptions.purchaseVerifiedBib ||
        scenarioOptions.autoHoldOffBib || scenarioOptions.autoHoldOffOutstandingPurchase ||
        scenarioOptions.autoHoldOnlyOutstandingPurchase ||
        scenarioOptions.verifyBibAndInvalidateIdentifier ||
        scenarioOptions.unverifiedPendingHold || scenarioOptions.verifiedPendingHold ||
        scenarioOptions.verificationInvalidation ||
        scenarioOptions.pickupWrappedResponse || scenarioOptions.stalePickup ? '9001' : null,
      bibidStaffVerified: Boolean(scenarioOptions.verificationInvalidation || scenarioOptions.verifiedUnchangedBib ||
        scenarioOptions.purchaseVerifiedBib || scenarioOptions.verifiedPendingHold),
      claimedByStaffUserId: scenarioOptions.unclaimedPreview ? null : scenarioOptions.autoClaimOther ? '21' : staff.id,
      claimType: scenarioOptions.formatWarning || scenarioOptions.autoClaimOther ? 'automatic_format_rule'
        : scenarioOptions.manualFormat ? 'manual' : null,
      publication: null, exactPublicationDate: '2026-01-02',
      notes: 'Editable note', autohold: Boolean(scenarioOptions.unverifiedPendingHold ||
        scenarioOptions.verifiedPendingHold || scenarioOptions.autoHoldOnlyOutstandingPurchase),
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
    const otherRequest = { ...request, id: otherId, title: 'Other request',
      status: scenarioOptions.stalePickup ? 'suggestion' : request.status, activity: [] };
    let payload = null;
    let committed = false;
    let mutationFailed = false;
    let postCommitQueueLoads = 0;
    let releaseTemplate = null;
    let releasePickup = null;
    let releaseMutation = null;
    let releaseFollowupQueue = null;
    global.fetch = async (url, options = {}) => {
      if (url.endsWith('/session')) {
        return response(200, { authenticated: true, accessAllowed: true, antiforgeryToken: 'token', staff });
      }
      if (url.includes('/title-requests?')) {
        if (mutationFailed && scenarioOptions.mutationNetworkFailureAfter401) {
          return response(401, { code: 'staff_session_invalid' });
        }
        if (committed && (scenarioOptions.laterUnrelated401 || scenarioOptions.laterUnrelated403)) {
          postCommitQueueLoads += 1;
          if (postCommitQueueLoads > (scenarioOptions.supersededFollowup ? 2 : 1)) {
            return scenarioOptions.laterUnrelated403
              ? response(403, { code: 'staff_scope_forbidden', accessAllowed: false })
              : response(401, { code: 'staff_session_invalid' });
          }
          if (postCommitQueueLoads === 1 &&
              (scenarioOptions.closeDuringFollowup || scenarioOptions.supersededFollowup)) {
            return new Promise(resolve => { releaseFollowupQueue = resolve; });
          }
          if (postCommitQueueLoads === 1 && scenarioOptions.followupDependencyAbort) {
            throw Object.assign(new Error('Queue dependency aborted'), { name: 'AbortError' });
          }
        }
        if (committed && scenarioOptions.queue401) {
          return response(401, { code: 'staff_session_invalid' });
        }
        if (committed && scenarioOptions.queue403) {
          return response(403, { code: 'staff_scope_forbidden', accessAllowed: false });
        }
        return response(200, { scope: scenarioOptions.supersededFollowup && !url.includes('scope=2') ? 'all' : '2',
          organizations: scenarioOptions.supersededFollowup ? [{ id: 2, name: 'Library' }] : [],
          items: scenarioOptions.staleChoice || scenarioOptions.staleMutation
            || scenarioOptions.stalePickup
            ? [currentRequest, otherRequest] : [currentRequest] });
      }
      if (url.endsWith(`/title-requests/${id}`)) {
        return committed && scenarioOptions.detailRefreshFails
          ? response(503, { message: 'Detail unavailable' })
          : response(200, currentRequest);
      }
      if (url.endsWith(`/title-requests/${otherId}`)) return response(200, otherRequest);
      if (url.endsWith(`/title-requests/${id}/pickup-options`)) {
        if (scenarioOptions.stalePickup) {
          return new Promise(resolve => { releasePickup = resolve; });
        }
        return response(200, { pickupBranches: [{ id: 10, name: 'Main' }],
          selectedPickupBranchId: 10, currentPreferredPickupBranchId: 10,
          version: 'v1', readOnly: false });
      }
      if (url.endsWith(`/title-requests/${id}/pickup-preference`) && options.method === 'POST') {
        payload = JSON.parse(options.body);
        currentRequest = { ...currentRequest, version: 'v2', activity: [...currentRequest.activity, {
          id: '9007199254741000', eventType: 'pickup_changed', actorType: 'staff',
          actorName: 'Staff', message: 'Pickup changed', created: '2026-01-02T00:00:00Z'
        }] };
        committed = true;
        return response(200, { request: currentRequest, pickupChanged: true, snapshotChanged: false });
      }
      if (url.endsWith(`/title-requests/${id}/place-hold`) && options.method === 'POST' &&
          scenarioOptions.holdTimeout) {
        return response(503, { code: 'hold_outcome_unconfirmed',
          message: 'The hold outcome could not be confirmed. Review the operation before retrying.' });
      }
      if (url.endsWith(`/title-requests/${id}/place-hold`) && options.method === 'POST' &&
          scenarioOptions.holdProviderError) {
        return response(502, { code: 'hold_provider_error',
          message: 'The hold provider outcome could not be confirmed. Review the operation before retrying.' });
      }
      if (url.endsWith('/bib-lookup') && options.method === 'POST') {
        return response(200, { bibId: '9001', title: 'Original title', author: null });
      }
      if (url.includes('/api/asap/config?')) return response(200, configuration);
      if (url.includes('/research-configuration')) return response(200, { externalSearchProviders: [] });
      if (url.endsWith(`/title-requests/${id}/rejection-templates`)) {
        if (scenarioOptions.templateDependencyAbort) {
          throw Object.assign(new Error('Template dependency aborted'), { name: 'AbortError' });
        }
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
        if (scenarioOptions.mutationDependencyAbort) {
          throw Object.assign(new Error('Dependency aborted'), { name: 'AbortError' });
        }
        if (scenarioOptions.mutationNetworkFailure) {
          throw new TypeError('Connection lost after submission');
        }
        if (scenarioOptions.mutationNetworkFailureAfter401) {
          mutationFailed = true;
          throw new TypeError('Connection lost after submission');
        }
        const finalStatus = action === 'reject' ? 'closed' : action === 'alreadyOwn' || scenarioOptions.purchaseVerifiedBib ? 'pending_hold' : 'outstanding_purchase';
        currentRequest = { ...currentRequest, version: 'v2', status: finalStatus,
          activity: [...currentRequest.activity, {
          id: '9007199254740999', eventType: 'status_changed', actorType: 'staff',
          actorName: 'Staff', message: 'Committed', created: '2026-01-02T00:00:00Z'
        }] };
        committed = true;
        const result = response(200, { committed: true,
          request: scenarioOptions.detailRefreshFails ? null : currentRequest, finalStatus,
          notificationStatus: scenarioOptions.rejectEmailQueued ? 'queued'
            : action === 'purchase' && explicitChoice && !scenarioOptions.purchaseVerifiedBib ? 'suppressed' : 'not_requested',
          notificationReason: action === 'purchase' && explicitChoice && !scenarioOptions.purchaseVerifiedBib ? 'mail_not_configured' : null,
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
    if (scenarioOptions.autoClaimOther) {
      assert.match(document.querySelector('.pending-audit-preview').textContent, /claim transfer/);
      const notes = document.querySelector('.edit-form textarea');
      notes.value = 'Updated note';
      notes.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
      const form = document.querySelector('.edit-form');
      form.dispatchEvent(new dom.window.Event('submit', { bubbles: true, cancelable: true }));
      assert.equal(payload, null, 'cancelled automatic-claim transfer must not mutate');
      assert.match(confirmMessages[0], /transfers the automatic format claim from the current claimant to your manual claim/);
      form.dispatchEvent(new dom.window.Event('submit', { bubbles: true, cancelable: true }));
      await until(() => payload !== null, 'automatic claim edit submitted');
      assert.equal(payload.notes, 'Updated note');
      await until(() => /Request changes saved/.test(document.querySelector('#app-status').textContent),
        'automatic claim edit result rendered');
      return;
    }
    assert.match(document.querySelector('.pending-audit-preview').textContent, /No pending changes/);
    assert.equal(document.querySelector('.edit-form button[type="submit"]').disabled, true);
    if (scenarioOptions.unverifiedPendingHold || scenarioOptions.verifiedPendingHold) {
      const placeHold = [...document.querySelectorAll('.action-bar button')]
        .find(item => item.textContent.includes('Place hold'));
      assert.equal(Boolean(placeHold), Boolean(scenarioOptions.verifiedPendingHold));
      if (scenarioOptions.holdTimeout || scenarioOptions.holdProviderError) {
        placeHold.click();
        await until(() => /hold outcome could not be confirmed/i.test(document.querySelector('#app-status').textContent),
          'hold provider uncertainty reports an unconfirmed result');
        assert.equal(payload, null, 'hold failure must not submit a title action');
      }
      return;
    }
    if (scenarioOptions.autoHoldOnlyOutstandingPurchase) {
      const checkbox = document.querySelector('.edit-form input[type="checkbox"]');
      checkbox.checked = false;
      checkbox.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
      const form = document.querySelector('.edit-form');
      form.dispatchEvent(new dom.window.Event('submit', { bubbles: true, cancelable: true }));
      assert.equal(payload, null, 'cancelled auto-hold-only change must not mutate');
      assert.match(confirmMessages[0], /automatic purchase promotion may later close it without a hold/);
      form.dispatchEvent(new dom.window.Event('submit', { bubbles: true, cancelable: true }));
      await until(() => payload !== null, 'confirmed auto-hold-only change submitted');
      assert.equal(payload.autohold, false);
      assert.equal(payload.staffSelectedBibId, undefined);
      return;
    }
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

    if (scenarioOptions.verifyBib || scenarioOptions.verifiedUnchangedBib || scenarioOptions.autoHoldOffBib ||
        scenarioOptions.autoHoldOffOutstandingPurchase ||
        scenarioOptions.purchaseVerifiedBib || scenarioOptions.verifyBibAndInvalidateIdentifier) {
      [...document.querySelectorAll('.edit-form button')]
        .find(item => item.textContent.includes('Search Polaris catalog')).click();
      await until(() => document.querySelector('#polaris-dialog').open, 'Polaris lookup opens');
      document.querySelector('#polaris-form').dispatchEvent(
        new dom.window.Event('submit', { bubbles: true, cancelable: true }));
      await until(() => document.querySelector('#polaris-results button'), 'BIB result appears');
      document.querySelector('#polaris-results button').click();
      if (scenarioOptions.verifiedUnchangedBib) {
        assert.match(document.querySelector('.pending-audit-preview').textContent, /No pending changes/);
        assert.equal(document.querySelector('.edit-form button[type="submit"]').disabled, true);
        [...document.querySelectorAll('.action-bar button')]
          .find(item => item.textContent.includes('Already own')).click();
        await until(() => payload !== null, 'unchanged verified BIB can enter Pending hold');
        assert.equal(payload.action, 'alreadyOwn');
        assert.match(confirmMessages[0], /Pending hold/);
        return;
      }
      if (!scenarioOptions.purchaseVerifiedBib) {
        await until(() => /BIB verification/.test(document.querySelector('.pending-audit-preview').textContent),
          'same-BIB Polaris selection previews verification');
        if (scenarioOptions.autoHoldOffBib || scenarioOptions.autoHoldOffOutstandingPurchase) {
          const form = document.querySelector('.edit-form');
          form.dispatchEvent(new dom.window.Event('submit', { bubbles: true, cancelable: true }));
          assert.equal(payload, null, 'cancelled no-auto-hold consequence must not mutate');
          assert.match(confirmMessages[0], /No hold will be placed automatically/);
          if (scenarioOptions.autoHoldOffOutstandingPurchase) {
            assert.match(confirmMessages[0], /automatic purchase promotion may later close it without a hold/);
          }
          form.dispatchEvent(new dom.window.Event('submit', { bubbles: true, cancelable: true }));
          await until(() => payload !== null, 'confirmed BIB save submitted');
          assert.equal(payload.autohold, false);
          assert.equal(payload.staffSelectedBibId, '9001');
          await until(() => /Request changes saved/.test(document.querySelector('#app-status').textContent),
            'confirmed BIB save result rendered');
          return;
        }
        if (scenarioOptions.verifyBibAndInvalidateIdentifier) {
          const identifier = document.querySelector('.edit-form input[maxlength="100"]');
          identifier.value = '9780000000002';
          identifier.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
          assert.equal(document.querySelector('.polaris-selection-context').textContent, '');
        }
        return;
      }
    }
    if (scenarioOptions.verificationInvalidation) {
      const bib = document.querySelector('.edit-form input[inputmode="numeric"]');
      bib.value = '';
      bib.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
      assert.match(document.querySelector('.pending-audit-preview').textContent, /BIB ID, BIB verification/);
      return;
    }
    if (scenarioOptions.formatWarning || scenarioOptions.manualFormat) {
      const format = document.querySelector('.edit-form select[aria-label="Format"]');
      format.value = 'ebook';
      format.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
      const form = document.querySelector('.edit-form');
      form.dispatchEvent(new dom.window.Event('submit', { bubbles: true, cancelable: true }));
      if (scenarioOptions.formatWarning) {
        assert.equal(payload, null, 'cancelled format warning must not mutate');
        assert.match(confirmMessages[0], /replaces the automatic format claim with your manual claim/);
        form.dispatchEvent(new dom.window.Event('submit', { bubbles: true, cancelable: true }));
      } else {
        assert.deepEqual(confirmMessages, [], 'manual claim must not trigger automatic-claim warning');
      }
      await until(() => payload !== null, 'format edit submitted');
      assert.equal(payload.format, 'ebook');
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

    if (scenarioOptions.pickupWrappedResponse || scenarioOptions.stalePickup) {
      [...document.querySelectorAll('.action-bar button')]
        .find(item => item.textContent.includes('Pickup')).click();
      if (scenarioOptions.stalePickup) {
        await until(() => releasePickup, 'first pickup request starts');
        await openOtherRequest();
        releasePickup(response(200, { pickupBranches: [{ id: 10, name: 'Main' }],
          selectedPickupBranchId: 10, currentPreferredPickupBranchId: 10,
          version: 'v1', readOnly: false }));
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(document.querySelector('.inline-form'), null,
          'stale pickup choices must not appear in another request');
        assert.equal(payload, null, 'stale pickup choices must not submit');
      } else {
        await until(() => document.querySelector('.inline-form select[aria-label="Preferred pickup branch"]'),
          'pickup choices load');
        document.querySelector('.inline-form').dispatchEvent(
          new dom.window.Event('submit', { bubbles: true, cancelable: true }));
        await until(() => payload !== null, 'pickup preference submits');
        await until(() => document.querySelector('.request-activity [data-event-id="9007199254741000"]'),
          'wrapped pickup response renders authoritative detail');
        assert.equal(document.querySelector('#request-dialog-title').textContent, 'Original title');
        assert.doesNotMatch(document.querySelector('#app-status').textContent, /could not be updated/);
      }
      return;
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
      if (scenarioOptions.templateDependencyAbort) {
        await until(() => /Template dependency aborted/.test(document.querySelector('#app-status').textContent),
          'uncancelled template dependency abort is announced');
        assert.equal(choice.querySelector('button[type="submit"]').disabled, true);
        assert.equal(payload, null);
        return;
      }
      await until(() => !choice.querySelector('button[type="submit"]').disabled, 'templates load');
      const select = choice.querySelector('select');
      assert.equal(select.options[1].value, templateId);
      assert.equal(choice.querySelector('img'), null);
      select.value = templateId;
    } else if (scenarioOptions.purchaseVerifiedBib) {
      assert.equal(choice.querySelector('input[type="checkbox"]'), null);
      assert.match(choice.textContent, /A purchase reminder does not apply/);
    } else {
      const checkbox = choice.querySelector('input[type="checkbox"]');
      assert.equal(checkbox.checked, profileDefault);
      checkbox.checked = explicitChoice;
    }
    choice.dispatchEvent(new dom.window.Event('submit', { bubbles: true, cancelable: true }));
    await until(() => payload !== null, 'action submitted');
    if (action === 'reject') assert.equal(payload.rejectionTemplateId, templateId);
    else assert.equal(payload.emailPurchaseReminder, scenarioOptions.purchaseVerifiedBib ? false : explicitChoice);
    if (scenarioOptions.purchaseVerifiedBib) {
      assert.match(confirmMessages.at(-1), /cannot place a hold until it is enabled/);
    }
    if (scenarioOptions.staleMutation) {
      await until(() => releaseMutation, 'purchase mutation starts');
      document.querySelector('#close-request').click();
      assert.equal(document.querySelector('#request-dialog').open, true,
        'in-flight workflow action must remain visible after Close');
      await until(() => document.querySelector(`[aria-label="Open request ${otherId}"]`),
        'newer request is present in the queue');
      document.querySelector(`[aria-label="Open request ${otherId}"]`).click();
      await until(() => document.querySelector('#request-dialog-title').textContent === 'Other request',
        'forced newer request takes ownership of the dialog');
      releaseMutation();
      await new Promise(resolve => setImmediate(resolve));
      assert.equal(document.querySelector('#request-dialog-title').textContent, 'Other request');
      assert.equal(document.querySelector('#request-dialog .status-badge').textContent, 'Suggestion');
      return;
    }
    if (scenarioOptions.mutationNetworkFailureAfter401) {
      await until(() => !document.querySelector('#signed-out').hidden,
        'unconfirmed action and immediate failed refresh sign out');
      assert.match(document.querySelector('#signed-out-message').textContent, /outcome could not be confirmed/);
      assert.match(document.querySelector('#signed-out-message').textContent, /authoritative result before retrying/);
      return;
    }
    if (scenarioOptions.mutationDependencyAbort || scenarioOptions.mutationNetworkFailure) {
      await until(() => /outcome could not be confirmed/.test(document.querySelector('#app-status').textContent),
        'uncancelled mutation dependency abort reports unconfirmed outcome');
      assert.equal(document.querySelector('#request-dialog .status-badge').textContent, 'Suggestion');
      return;
    }
    if (scenarioOptions.queue401 || scenarioOptions.queue403) {
      await until(() => !document.querySelector('#signed-out').hidden, 'session transition displays result');
      assert.match(document.querySelector('#signed-out-message').textContent, /Final state: Outstanding purchase/);
      assert.match(document.querySelector('#signed-out-message').textContent, /Purchase reminder suppressed/);
    } else if (scenarioOptions.laterUnrelated401 || scenarioOptions.laterUnrelated403) {
      await until(() => postCommitQueueLoads === 1, 'committed follow-up queue load starts');
      if (scenarioOptions.closeDuringFollowup) {
        await until(() => releaseFollowupQueue, 'delayed follow-up queue load starts');
        document.querySelector('#close-request').click();
        assert.equal(document.querySelector('#request-dialog').open, false,
          'the dialog may close after the committed detail renders, even while the queue refresh is in flight');
        releaseFollowupQueue(response(200, { scope: '2', organizations: [], items: [currentRequest] }));
        await until(() => !document.querySelector('#refresh-queue').disabled,
          'committed follow-up queue load completes after Close');
      } else if (scenarioOptions.supersededFollowup) {
        await until(() => releaseFollowupQueue, 'delayed follow-up queue load starts');
        const scope = document.querySelector('#library-scope');
        scope.value = '2';
        scope.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
        await until(() => postCommitQueueLoads === 2 && !document.querySelector('#refresh-queue').disabled,
          'newer scope queue load completes');
        releaseFollowupQueue(response(200, { scope: 'all', organizations: [], items: [currentRequest] }));
        await new Promise(resolve => setTimeout(resolve, 20));
        assert.doesNotMatch(document.querySelector('#app-status').textContent, /queue could not refresh/);
      }
      await until(() => !document.querySelector('#refresh-queue').disabled,
        'committed follow-up queue load completes');
      if (scenarioOptions.followupDependencyAbort) {
        await until(() => /queue could not refresh/.test(document.querySelector('#app-status').textContent),
          'uncancelled dependency abort is reported as a refresh failure');
      }
      document.querySelector('#refresh-queue').click();
      await until(() => !document.querySelector('#signed-out').hidden,
        'later unrelated session failure displays sign-out');
      if (scenarioOptions.detailRefreshFails || scenarioOptions.followupDependencyAbort) {
        assert.match(document.querySelector('#signed-out-message').textContent, /Final state: Outstanding purchase/);
      } else {
        assert.match(document.querySelector('#signed-out-message').textContent,
          /session ended|access is not currently available/);
        assert.doesNotMatch(document.querySelector('#signed-out-message').textContent, /Final state:/);
      }
    } else if (scenarioOptions.detailRefreshFails) {
      await until(() => /Details and activity could not refresh/.test(document.querySelector('#app-status').textContent),
        'committed outcome survives detail refresh failure');
      assert.match(document.querySelector('#request-dialog-body').textContent, /The action committed/);
    } else {
      await until(() => document.querySelectorAll('.request-activity li').length === 3,
        'committed activity renders once');
      assert.match(document.querySelector('#app-status').textContent, /Final state:/);
      if (scenarioOptions.rejectEmailQueued) {
        assert.match(document.querySelector('#app-status').textContent, /Rejection email queued/);
      }
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
  await scenario('purchase', true, true, { purchaseVerifiedBib: true });
  await scenario('reject', false, true);
  await scenario('reject', false, true, { rejectEmailQueued: true });
  await scenario('purchase', false, true, { queue401: true });
  await scenario('purchase', false, true, { queue403: true });
  await scenario('purchase', false, true, { laterUnrelated401: true });
  await scenario('purchase', false, true, { laterUnrelated401: true, closeDuringFollowup: true });
  await scenario('purchase', false, true, { laterUnrelated401: true, supersededFollowup: true });
  await scenario('purchase', false, true, { laterUnrelated403: true });
  await scenario('purchase', false, false, { detailRefreshFails: true, laterUnrelated401: true });
  await scenario('purchase', false, true, { laterUnrelated401: true, followupDependencyAbort: true });
  await scenario('purchase', false, false, { detailRefreshFails: true });
  await scenario('reject', false, false, { staleChoice: true });
  await scenario('purchase', false, true, { staleMutation: true });
  await scenario('reject', false, false, { mutationDependencyAbort: true });
  await scenario('reject', false, false, { mutationNetworkFailure: true });
  await scenario('reject', false, false, { mutationNetworkFailureAfter401: true });
  await scenario('reject', false, false, { templateDependencyAbort: true });
  await scenario('reject', false, false, { verifyBib: true });
  await scenario('alreadyOwn', false, false, { verifiedUnchangedBib: true });
  await scenario('reject', false, false, { verifyBibAndInvalidateIdentifier: true });
  await scenario('reject', false, false, { verificationInvalidation: true });
  await scenario('reject', false, false, { unverifiedPendingHold: true });
  await scenario('reject', false, false, { verifiedPendingHold: true });
  await scenario('reject', false, false, { verifiedPendingHold: true, holdTimeout: true });
  await scenario('reject', false, false, { verifiedPendingHold: true, holdProviderError: true });
  await scenario('reject', false, false, { autoHoldOffBib: true });
  await scenario('reject', false, false, { autoHoldOffOutstandingPurchase: true });
  await scenario('reject', false, false, { autoHoldOnlyOutstandingPurchase: true });
  await scenario('reject', false, false, { unclaimedPreview: true });
  await scenario('reject', false, false, { formatWarning: true });
  await scenario('reject', false, false, { autoClaimOther: true });
  await scenario('reject', false, false, { manualFormat: true });
  await scenario('purchase', false, false, { pickupWrappedResponse: true });
  await scenario('purchase', false, false, { stalePickup: true });
  console.log('Staff request action choice, preview, activity, safe text, and exact ID UI checks passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
