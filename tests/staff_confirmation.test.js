const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');
const { answerConfirmations } = require('./helpers/confirmation.cjs');

(async () => {
  for (const outcome of ['approve', 'cancel', 'escape', 'enter', 'close', 'dispose', 'stale', 'abort', 'removed', 'retired opener']) {
    await fixture(async ({ load, dom, get }) => {
      answerConfirmations(dom.window, () => null);
      const { createConfirmationController } = await load('confirmation');
      const controller = createConfirmationController();
      const opener = get('#close-request'), detail = get('#request-dialog');
      detail.showModal(); opener.focus();
      let current = true;
      const abort = new dom.window.AbortController();
      const decision = controller.ask({ title: 'Delete <b>request</b>?', message: 'A <script>value</script> stays text.',
        confirmLabel: 'Delete permanently', tone: 'danger', isCurrent: () => current, signal: abort.signal });
      await new Promise(resolve => dom.window.setTimeout(resolve, 0));
      const dialog = get('#confirmation-dialog');
      assert.equal(dialog.open, true); assert.equal(detail.open, true, 'confirmation has its own modal');
      assert.equal(dialog.querySelector('b, script'), null);
      assert.equal(document.activeElement, dialog.querySelector('button'), 'Cancel is initially focused');
      document.activeElement.dispatchEvent(new dom.window.KeyboardEvent('keydown', { key: 'Tab', shiftKey: true, bubbles: true, cancelable: true }));
      assert.equal(document.activeElement, dialog.querySelector('.danger-button'));
      document.activeElement.dispatchEvent(new dom.window.KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true }));
      assert.equal(document.activeElement, dialog.querySelector('button'), 'Tab wraps inside the modal');
      assert.equal(await controller.ask({ isCurrent: () => true }), false, 'competing decisions fail safely');
      if (outcome === 'approve') dialog.querySelector('.danger-button').click();
      if (outcome === 'cancel') dialog.querySelector('button').click();
      if (outcome === 'escape') dialog.dispatchEvent(new dom.window.Event('cancel', { cancelable: true }));
      if (outcome === 'enter') dialog.querySelector('button').dispatchEvent(new dom.window.KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }));
      if (outcome === 'close') { dialog.close(); dialog.dispatchEvent(new dom.window.Event('close')); }
      if (outcome === 'dispose') controller.dispose();
      if (outcome === 'stale') current = false;
      if (outcome === 'abort') abort.abort();
      if (outcome === 'removed') dialog.remove();
      if (outcome === 'retired opener') { detail.close(); opener.remove(); dialog.querySelector('button').click(); }
      assert.equal(await decision, outcome === 'approve');
      assert.equal(dialog.open, false);
      if (!['stale', 'retired opener', 'removed'].includes(outcome)) assert.equal(document.activeElement, opener);
      controller.dispose(); assert.equal(get('#confirmation-dialog'), null);
    });
  }
  for (const change of ['draft', 'actor', 'scope', 'pending', 'disposed', 'new navigation']) {
    await fixture(async ({ load, dom, get }) => {
      answerConfirmations(dom.window, () => null);
      const { createNavigationController } = await load('navigation');
      const { createSessionIdentity } = await load('session-identity');
      const identity = createSessionIdentity(); identity.accept({ id: '20', role: 'staff', organizationId: 2 });
      let stamp = 1, blocked = false, discarded = 0;
      const navigation = createNavigationController({ sessionIdentity: identity,
        router: { busy: () => false, dispose() {} }, views: {}, announce() {}, onInvalidate() {}, onContextChanged() {},
        getFeatures: () => [{ key: 'draft', inspectDeparture, discardDeparture: () => discarded++ }] });
      function inspectDeparture() { return { dirty: true, stamp, blocked, confirmMessage: 'Discard this exact draft?' }; }
      const preparing = navigation.prepareDeparture();
      await new Promise(resolve => dom.window.setTimeout(resolve, 0));
      assert.equal(get('#confirmation-dialog').open, true);
      if (change === 'draft') stamp++;
      if (change === 'actor') identity.clear();
      if (change === 'scope') navigation.align({ scope: '3' });
      if (change === 'pending') blocked = true;
      if (change === 'disposed') navigation.dispose();
      let newer;
      if (change === 'new navigation') { newer = navigation.prepareDeparture(); await new Promise(resolve => dom.window.setTimeout(resolve, 0)); }
      if (change !== 'disposed') get('#confirmation-dialog .danger-button').click();
      assert.equal(await preparing, null, `${change} cannot reuse consent`);
      assert.equal(discarded, 0);
      if (newer) { const departure = await newer; assert.ok(departure); assert.equal(departure.commit(), true); assert.equal(departure.commit(), false); }
      navigation.dispose();
    });
  }
  await fixture(async ({ load, dom, get }) => {
    answerConfirmations(dom.window, () => null);
    const { createProfileController } = await load('profile-controller');
    const { createNavigationController } = await load('navigation');
    const { createSessionIdentity } = await load('session-identity');
    const identity = createSessionIdentity(); const staff = identity.accept({ id: '20', role: 'staff', organizationId: 2 });
    const profile = createProfileController({ root: get('#profile-view'), sessionIdentity: identity, announce() {} });
    profile.setStaff(staff);
    const field = get('#weekly-email'); field.value = 'Draft';
    field.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    const navigation = createNavigationController({ sessionIdentity: identity,
      router: { busy: () => false, dispose() {} }, getFeatures: () => [{ key: 'profile', inspectDeparture: profile.inspectDeparture }],
      views: {}, announce() {} });
    const preparing = navigation.prepareDeparture();
    await new Promise(resolve => dom.window.setTimeout(resolve, 0));
    field.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    get('#confirmation-dialog .danger-button').click();
    const departure = await preparing;
    assert.ok(departure, 'a blur change for an already-recorded value must preserve consent');
    assert.equal(departure.commit(), true);
    profile.dispose(); navigation.dispose();
  });
  for (const final of ['suggestion', 'closed']) {
    await fixture(async ({ load }) => {
      const { createNavigationController } = await load('navigation');
      const { createSessionIdentity } = await load('session-identity');
      const identity = createSessionIdentity(); identity.accept({ id: '20', role: 'staff', organizationId: 2 });
      let changes = 0;
      const navigation = createNavigationController({ sessionIdentity: identity,
        router: { busy: () => false, pushStage() {}, remember() {}, dispose() {} },
        getFeatures: () => [], views: { queue: { isReady: () => true } }, announce() {}, closeTransient() {}, onInvalidate() {},
        onContextChanged() { changes++; } });
      const results = await Promise.all(['outstanding_purchase', 'pending_hold', final]
        .map(status => navigation.changeQueueContext('queue', { status })));
      assert.deepEqual(results, [false, false, true]);
      assert.equal(navigation.context().status, final);
      assert.equal(changes, final === 'suggestion' ? 0 : 1, 'only the latest intent can commit');
      navigation.dispose();
    });
  }
  await fixture(async ({ load, dom, get }) => {
    answerConfirmations(dom.window, () => null);
    const { createConfirmationController } = await load('confirmation');
    const controller = createConfirmationController();
    const detail = get('#request-dialog'); detail.showModal();
    const action = document.createElement('button'); detail.append(action); action.focus();
    const decision = controller.ask({ title: 'Continue?', message: 'Review this request.', confirmLabel: 'Continue', isCurrent: () => true });
    await new Promise(resolve => dom.window.setTimeout(resolve, 0));
    action.remove(); get('#confirmation-dialog button').click();
    assert.equal(await decision, false);
    assert.equal(document.activeElement, get('#close-request'), 'a surviving control in the same dialog receives focus');
    controller.dispose();
  });
  await fixture(async ({ load, dom, get }) => {
    answerConfirmations(dom.window, () => null);
    const { createConfirmationController } = await load('confirmation');
    const controller = createConfirmationController();
    let current = true;
    const old = controller.ask({ title: 'Old request', message: 'Old decision', confirmLabel: 'Continue', isCurrent: () => current });
    const retiredButton = get('#confirmation-dialog .primary-button');
    current = false;
    const next = controller.ask({ title: 'New request', message: 'New decision', confirmLabel: 'Continue', isCurrent: () => true });
    assert.equal(await old, false);
    retiredButton.click();
    assert.equal(get('#confirmation-dialog').open, true, 'a retained old button cannot approve the next decision');
    get('#confirmation-dialog .secondary-button').click();
    assert.equal(await next, false);
    controller.dispose();
  });
  console.log('21 confirmation keyboard, lifecycle, focus, stale-owner and departure cases passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
