const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');
const settle = async () => { for (let index = 0; index < 10; index += 1) await new Promise(resolve => setImmediate(resolve)); };
const deferred = () => { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };
const fingerprint = 'A'.repeat(64);
const saved = organizationId => ({ code: 'ok', data: { organizationId, scopeName: `<Library ${organizationId}>`,
  fromAddress: 'saved@example.org', fromName: 'Saved sender', recipient: 'primary@example.org',
  deliveryMode: 'postmark', state: 'ready', blockingReason: null, configurationVersion: fingerprint } });
const accepted = { code: 'queued', data: { id: '9007199254740993', version: 'outbox-v1', dispatchDelayed: false } };
const notFound = () => Object.assign(new Error('No recorded test'), { status: 404, response: { code: 'not_found' } });
const recoveryFixture = journey => fixture(async environment => {
  const originalNow = Date.now;
  let now = originalNow();
  Date.now = () => now;
  try { await journey({ ...environment, advance: milliseconds => { now += milliseconds; } }); }
  finally { Date.now = originalNow; }
});

(async () => {
  await fixture(async ({ load, get }) => {
    const { createEmailTestController, emailTestStatusMessage } = await load('email-test');
    let value = { ownerKey: 'actor-a', organizationId: 2, active: true, ready: true, dirty: false, blocked: false };
    const calls = [], posts = [], pendingPost = deferred();
    const root = get('#settings-email-test');
    const button = root.querySelector('[data-email-test="send"]');
    const result = root.querySelector('[data-email-test="result"]');
    let status = { id: '9007199254740993', organizationId: 2, status: 'sent', deliveryMode: 'postmark' };
    const controller = createEmailTestController({ root, context: () => value, request: async (path, init = {}) => {
      calls.push({ path, init });
      if (init.method === 'POST') { posts.push({ path, init }); return pendingPost.promise; }
      if (path.includes('/test-context')) return saved(Number(new URL(path, 'https://localhost').searchParams.get('organizationId')));
      return { code: 'ok', data: status };
    }, pollInterval: 5, observationLimit: 20 });
    assert.equal(button.disabled, true);
    controller.sync(); await settle();
    assert.equal(button.disabled, false);
    assert.match(root.textContent, /<Library 2>/);
    assert.equal(root.querySelector('Library'), null, 'saved runtime scope text cannot create markup');
    assert.match(root.textContent, /Saved sender <saved@example.org>/);
    assert.match(root.textContent, /primary@example.org/);
    value = { ...value, dirty: true }; controller.sync();
    assert.equal(button.disabled, true);
    assert.match(result.textContent, /Save or discard/);
    await controller.send(); assert.equal(posts.length, 0, 'dirty settings cannot queue a test');
    value = { ...value, dirty: false }; controller.sync();
    const sending = controller.send();
    button.click();
    assert.equal(posts.length, 1, 'double activation creates one request');
    assert.deepEqual(posts[0].init.body, { configurationVersion: fingerprint, intendedDeliveryMode: 'postmark' });
    assert.equal(new URL(posts[0].path, 'https://localhost').searchParams.get('organizationId'), '2');
    assert.match(result.textContent, /Requesting test email/);
    pendingPost.resolve(accepted); await sending; await settle();
    assert.match(result.textContent, /email service accepted/);
    assert.doesNotMatch(result.textContent, /delivered to|received in/i);
    assert.match(result.textContent, /9007199254740993/);
    assert.equal(posts.length, 1);
    assert.equal(button.disabled, false);
    assert.ok(calls.some(call => call.path === '/api/asap/staff/email-operations/9007199254740993?organizationId=2'));
    status = { ...status, deliveryMode: 'file' };
    await controller.refresh();
    assert.match(result.textContent, /local test message.*No email was sent/);
    for (const code of ['provider_timeout', 'transport_failure', 'ambiguous_expired_lease']) {
      status = { ...status, status: 'failed', lastErrorCode: code };
      await controller.refresh();
      assert.match(result.textContent, /Acceptance is unconfirmed/);
      assert.equal(button.disabled, true, 'an uncertain provider outcome cannot silently authorize a fresh test');
    }
    assert.match(emailTestStatusMessage({ id: '1', status: 'sent', deliveryMode: 'unknown' }), /unverified/);
    controller.dispose();
  });

  await fixture(async ({ load, get, dom }) => {
    const { createEmailTestController } = await load('email-test');
    let value = { ownerKey: 'actor-b', organizationId: 2, active: true, ready: true, dirty: false, blocked: false };
    const root = get('#operations-email-test'), calls = [], pending = deferred();
    let fail = true;
    const controller = createEmailTestController({ root, context: () => value, request: async (path, init = {}) => {
      calls.push({ path, init });
      if (path.includes('/test-context')) return saved(value.organizationId);
      if (init.method === 'POST') return pending.promise;
      if (fail) throw Object.assign(new Error('not yet visible'), { status: 404 });
      return { code: 'ok', data: { id: '9223372036854775807', organizationId: 2, status: 'sent', deliveryMode: 'postmark' } };
    } });
    controller.sync(); await settle();
    const sending = controller.send();
    value = { ...value, organizationId: 3 }; controller.sync(); await settle();
    pending.reject(new Error('lost acknowledgement')); await sending;
    assert.doesNotMatch(root.textContent, /lost acknowledgement|actor-b/);
    value = { ...value, organizationId: 2 }; controller.sync(); await settle();
    assert.equal(calls.filter(call => call.init.method === 'POST').length, 1);
    assert.ok(calls.some(call => /\/email-operations\/test\?organizationId=2&operationId=/.test(call.path)),
      'a lost acknowledgement is recovered by the original click ID, without POST replay');
    assert.equal(root.querySelector('[data-email-test="send"]').disabled, true);
    assert.match(root.querySelector('[data-email-test="result"]').textContent, /unconfirmed/);
    const stored = [...Array(dom.window.sessionStorage.length)].map((_, index) =>
      JSON.parse(dom.window.sessionStorage.getItem(dom.window.sessionStorage.key(index)))).find(row => row.operationId);
    assert.equal(stored.pending, true);
    fail = false; await controller.refresh();
    assert.equal(root.querySelector('[data-email-test="send"]').disabled, false);
    assert.equal(calls.filter(call => call.init.method === 'POST').length, 1);
    controller.dispose();
  });

  await fixture(async ({ load, get }) => {
    const { createEmailTestController } = await load('email-test');
    let value = { ownerKey: 'capture-actor', organizationId: 2, active: true, ready: true, dirty: false, blocked: false };
    const root = get('#settings-email-test'), pending = deferred();
    let requestCount = 0;
    const controller = createEmailTestController({ root, context: () => value, request: async () => {
      requestCount += 1;
      return requestCount === 1 ? pending.promise : { ...saved(2), data: { ...saved(2).data,
        state: 'blocked', deliveryMode: 'file', blockingReason: 'live_delivery_disabled' } };
    } });
    controller.sync();
    value = { ...value, dirty: true }; controller.sync();
    pending.resolve(saved(2)); await settle();
    assert.equal(root.querySelector('[data-email-test="send"]').disabled, true);
    value = { ...value, dirty: false }; controller.sync(); await settle();
    assert.equal(requestCount, 2, 'discarding edits restarts an aborted saved-context load');
    assert.equal(root.querySelector('[data-email-test="send"]').disabled, true);
    assert.match(root.textContent, /Live email sending is disabled/);
    value = { ...value, active: false }; controller.sync();
    controller.dispose();
  });
  for (const status of [200, 400]) {
    await fixture(async ({ load, get }) => {
      const { createEmailTestController } = await load('email-test');
      const value = { ownerKey: `ack-${status}`, organizationId: 2, active: true, ready: true, dirty: false, blocked: false };
      const root = get('#settings-email-test');
      let posts = 0, observations = 0;
      const options = { root, context: () => value, request: async (path, init = {}) => {
        if (path.includes('/test-context')) return saved(2);
        if (init.method === 'POST') {
          posts += 1;
          throw Object.assign(new Error('Controlled acknowledgement'), { status,
            outcomeUnknown: status === 200, response: { code: 'email_test_configuration_changed' } });
        }
        observations += 1;
        throw Object.assign(new Error('No recorded reference yet'), { status: 404 });
      } };
      const controller = createEmailTestController(options);
      controller.sync(); await settle(); await controller.send();
      assert.equal(root.querySelector('[data-email-test="send"]').disabled, true);
      if (status === 200) assert.match(root.textContent, /outcome is unconfirmed/);
      else assert.match(root.textContent, /configuration|account changed/);
      controller.dispose();
      const restored = createEmailTestController(options); restored.sync(); await settle();
      assert.equal(posts, 1);
      assert.equal(observations, status === 200 ? 1 : 0,
        'only an uncertain acknowledgement needs by-click observation; a definite rejection is never relabeled unknown');
      if (status === 200) {
        await restored.send(); assert.equal(posts, 1);
        assert.equal(root.querySelector('[data-email-test="send"]').disabled, true);
      }
      restored.dispose();
    });
  }
  await fixture(async ({ load, get }) => {
    const { createEmailTestController } = await load('email-test');
    let value = { ownerKey: 'bounded-observer', organizationId: 2, active: true, ready: true, dirty: false, blocked: false };
    const root = get('#settings-email-test'), paused = deferred();
    let posts = 0, observations = 0;
    const controller = createEmailTestController({ root, context: () => value, observationLimit: 0, pollInterval: 5,
      request: async (path, init = {}) => {
        if (path.includes('/test-context')) return saved(2);
        if (init.method === 'POST') { posts += 1; return accepted; }
        observations += 1;
        if (observations === 2) return paused.promise;
        return { code: 'ok', data: { id: accepted.data.id, organizationId: 2,
          status: observations === 1 ? 'pending' : 'sent', deliveryMode: 'postmark' } };
      } });
    controller.sync(); await settle(); await controller.send(); await settle();
    assert.match(root.querySelector('[data-email-test="result"]').textContent, /Still waiting.*Reference/);
    assert.equal(root.querySelector('[data-email-test="send"]').disabled, true);
    await new Promise(resolve => setTimeout(resolve, 20));
    assert.equal(observations, 1, 'the automatic observer stops at its deadline without requesting another message');
    const manual = controller.refresh();
    value = { ...value, active: false }; controller.sync();
    paused.resolve({ code: 'ok', data: { id: accepted.data.id, organizationId: 2,
      status: 'sent', deliveryMode: 'postmark' } });
    await manual;
    assert.doesNotMatch(root.textContent, /email service accepted/, 'panel exit retires an in-flight status reply');
    value = { ...value, active: true }; controller.sync(); await settle();
    assert.match(root.textContent, /email service accepted/);
    await new Promise(resolve => setTimeout(resolve, 20));
    assert.equal(observations, 3, 'a terminal result stops automatic observation');
    assert.equal(posts, 1, 'manual status refresh and panel restoration never POST the accepted intent again');
    controller.dispose();
  });
  await recoveryFixture(async ({ load, get, dom, advance }) => {
    const { createEmailTestController } = await load('email-test');
    const value = { ownerKey: 'never-recorded', organizationId: 2, active: true, ready: true, dirty: false, blocked: false };
    const root = get('#settings-email-test'), posts = [], lookups = [];
    const options = { root, context: () => value, request: async (path, init = {}) => {
      if (path.includes('/test-context')) return saved(2);
      if (init.method === 'POST') {
        posts.push(path);
        throw new Error('Submission never reached the server');
      }
      lookups.push(path);
      throw notFound();
    } };
    let controller = createEmailTestController(options);
    controller.sync(); await settle(); await controller.send();
    const storageKey = 'asap.staff.email-test.never-recorded.2';
    const original = JSON.parse(dom.window.sessionStorage.getItem(storageKey));
    assert.equal(original.pending, true);
    assert.ok(posts[0].includes(original.operationId));
    await controller.refresh();
    assert.equal(root.querySelector('[data-email-test="send"]').disabled, true);
    controller.dispose();
    controller = createEmailTestController(options); controller.sync(); await settle();
    assert.ok(lookups.every(path => path.includes(original.operationId)), 'recreation reconciles the original intent');
    const retire = root.querySelector('[data-email-test="retire"]');
    assert.ok(retire, 'the supported UI must offer explicit retirement after authoritative absence checks');
    assert.equal(retire.hidden, true, 'a single 404 after recreation is insufficient');
    advance(60001); await controller.refresh();
    assert.equal(retire.hidden, false);
    assert.equal(root.querySelector('[data-email-test="send"]').disabled, true);
    assert.match(root.textContent, /delayed request|may still arrive/i);
    const readsBeforeRetirement = lookups.length;
    retire.click(); await settle();
    assert.equal(lookups.length, readsBeforeRetirement + 1, 'retirement performs a final authoritative lookup');
    assert.equal(posts.length, 1, 'retirement itself never submits a test');
    assert.equal(root.querySelector('[data-email-test="send"]').disabled, false);
    const retired = JSON.parse(dom.window.sessionStorage.getItem(storageKey));
    assert.equal(retired.operationId, original.operationId);
    assert.equal(retired.retired, true);
    assert.match(root.querySelector('[data-email-test="history"]').textContent, new RegExp(original.operationId));
    controller.dispose();
    controller = createEmailTestController(options); controller.sync(); await settle();
    assert.equal(root.querySelector('[data-email-test="send"]').disabled, false);
    root.querySelector('[data-email-test="send"]').click(); await settle();
    assert.equal(posts.length, 2, 'only a separate intentional send creates another POST');
    const next = JSON.parse(dom.window.sessionStorage.getItem(storageKey));
    assert.notEqual(next.operationId, original.operationId);
    assert.ok(posts[1].includes(next.operationId));
    assert.equal(next.pending, true);
    assert.match(root.querySelector('[data-email-test="history"]').textContent, new RegExp(original.operationId));
    controller.dispose();
  });
  for (const row of [
    { status: 'pending' }, { status: 'sending' }, { status: 'sent' }, { status: 'suppressed' },
    { status: 'failed', lastErrorCode: 'recipient_invalid' },
    ...['provider_timeout', 'transport_failure', 'ambiguous_expired_lease', 'provider_start_deadline_exceeded', null]
      .map(lastErrorCode => ({ status: 'failed', lastErrorCode }))
  ]) {
    await recoveryFixture(async ({ load, get, dom, advance }) => {
      const { createEmailTestController } = await load('email-test');
      const value = { ownerKey: 'delayed-record', organizationId: 2, active: true, ready: true, dirty: false, blocked: false };
      const root = get('#operations-email-test');
      let visible = false, posts = 0;
      const controller = createEmailTestController({ root, context: () => value, observationLimit: 0,
        request: async (path, init = {}) => {
          if (path.includes('/test-context')) return saved(2);
          if (init.method === 'POST') { posts += 1; throw new Error('Lost submission'); }
          if (!visible) throw notFound();
          return { code: 'ok', data: { id: accepted.data.id, organizationId: 2, deliveryMode: 'postmark', ...row } };
        } });
      controller.sync(); await settle(); await controller.send(); await controller.refresh();
      advance(60001); await controller.refresh();
      const retire = root.querySelector('[data-email-test="retire"]');
      assert.equal(retire.hidden, false);
      visible = true; retire.click(); await settle();
      const stored = JSON.parse(dom.window.sessionStorage.getItem('asap.staff.email-test.delayed-record.2'));
      assert.equal(stored.id, accepted.data.id, 'the final lookup recovers a delayed committed record');
      assert.equal(stored.retired, false, 'an existing outbox row is never retired by the client');
      assert.equal(root.querySelector('[data-email-test="send"]').disabled, stored.pending);
      assert.equal(stored.pending, ['pending', 'sending'].includes(row.status) ||
        row.status === 'failed' && row.lastErrorCode !== 'recipient_invalid');
      assert.equal(retire.hidden, true);
      assert.equal(root.querySelector('[data-email-test="history"]').hidden, true);
      visible = false; await controller.refresh(); advance(60001); await controller.refresh();
      assert.equal(retire.hidden, true, '404 never permits retirement of an already-known durable record');
      assert.equal(posts, 1);
      controller.dispose();
    });
  }
  for (const failure of [
    () => new Error('Offline'),
    () => Object.assign(new Error('Unrelated 404'), { status: 404 }),
    () => Object.assign(new Error('Different 404'), { status: 404, response: { code: 'organization_not_found' } }),
    ...[401, 403, 500].map(status => () => Object.assign(new Error('Unavailable'), { status })),
    () => null
  ]) {
    await recoveryFixture(async ({ load, get, advance }) => {
      const { createEmailTestController } = await load('email-test');
      const value = { ownerKey: 'no-proof', organizationId: 2, active: true, ready: true, dirty: false, blocked: false };
      const root = get('#settings-email-test');
      let fail = false, posts = 0;
      const controller = createEmailTestController({ root, context: () => value, request: async (path, init = {}) => {
        if (path.includes('/test-context')) return saved(2);
        if (init.method === 'POST') { posts += 1; throw new Error('Lost submission'); }
        if (fail) { const error = failure(); if (error) throw error; return { code: 'ok', data: { id: 1 } }; }
        throw notFound();
      } });
      controller.sync(); await settle(); await controller.send(); await controller.refresh();
      advance(60001); fail = true; await controller.refresh();
      assert.equal(root.querySelector('[data-email-test="retire"]').hidden, true, 'failed reconciliation resets absence evidence');
      fail = false; await controller.refresh();
      assert.equal(root.querySelector('[data-email-test="retire"]').hidden, true, 'absence must be re-established after a failure');
      advance(60001); await controller.refresh();
      assert.equal(root.querySelector('[data-email-test="retire"]').hidden, false);
      fail = true; root.querySelector('[data-email-test="retire"]').click(); await settle();
      assert.equal(root.querySelector('[data-email-test="send"]').disabled, true, 'failed final reconciliation never unblocks a send');
      assert.equal(root.querySelector('[data-email-test="retire"]').hidden, true);
      assert.equal(posts, 1);
      controller.dispose();
    });
  }
  for (const change of ['scope', 'actor', 'session']) {
    await recoveryFixture(async ({ load, get, dom, advance }) => {
      const { createEmailTestController } = await load('email-test');
      let value = { ownerKey: 'recovery-owner', organizationId: 2, active: true, ready: true, dirty: false, blocked: false };
      const root = get('#settings-email-test'), finalRead = deferred(), posts = [];
      let pause = false;
      const controller = createEmailTestController({ root, context: () => value, request: async (path, init = {}) => {
        if (path.includes('/test-context')) return saved(value.organizationId);
        if (init.method === 'POST') { posts.push(path); throw new Error('Never submitted'); }
        if (pause) return finalRead.promise;
        throw notFound();
      } });
      controller.sync(); await settle(); await controller.send(); await controller.refresh();
      advance(60001); await controller.refresh();
      pause = true; root.querySelector('[data-email-test="retire"]').click(); await settle();
      const original = JSON.parse(dom.window.sessionStorage.getItem('asap.staff.email-test.recovery-owner.2'));
      value = { ...value, ...(change === 'scope' ? { organizationId: 3 }
        : change === 'actor' ? { ownerKey: 'replacement-owner' } : { active: false }) };
      controller.sync(); await settle();
      pause = false;
      if (change !== 'session') { root.querySelector('[data-email-test="send"]').click(); await settle(); }
      finalRead.reject(notFound()); await settle();
      assert.equal(JSON.parse(dom.window.sessionStorage.getItem('asap.staff.email-test.recovery-owner.2')).pending, true);
      assert.equal(dom.window.sessionStorage.getItem('asap.staff.email-test.recovery-owner.2.retirement-references'), null);
      assert.doesNotMatch(root.textContent, /Retired the unrecorded/);
      if (change !== 'session') {
        const next = JSON.parse(dom.window.sessionStorage.getItem(`asap.staff.email-test.${value.ownerKey}.${value.organizationId}`));
        assert.equal(next.pending, true);
        assert.notEqual(next.operationId, original.operationId);
      }
      value = { ...value, ownerKey: 'recovery-owner', organizationId: 2, active: true };
      controller.sync(); await settle();
      assert.equal(root.querySelector('[data-email-test="send"]').disabled, true);
      assert.equal(root.querySelector('[data-email-test="retire"]').hidden, true, 'a changed owner requires fresh reconciliation');
      assert.equal(posts.length, change === 'session' ? 1 : 2);
      controller.dispose();
    });
  }
  await recoveryFixture(async ({ load, get, dom, advance }) => {
    const { createEmailTestController } = await load('email-test');
    const value = { ownerKey: 'stale-status', organizationId: 2, active: true, ready: true, dirty: false, blocked: false };
    const root = get('#settings-email-test'), oldRead = deferred();
    let posts = 0, reads = 0;
    const controller = createEmailTestController({ root, context: () => value, request: async (path, init = {}) => {
      if (path.includes('/test-context')) return saved(2);
      if (init.method === 'POST') { posts += 1; throw new Error('Unrecorded'); }
      reads += 1; if (reads === 2) return oldRead.promise;
      throw notFound();
    } });
    controller.sync(); await settle(); await controller.send(); await controller.refresh();
    const stale = controller.refresh();
    advance(60001); await controller.refresh();
    root.querySelector('[data-email-test="retire"]').click(); await settle();
    root.querySelector('[data-email-test="send"]').click(); await settle();
    const storageKey = 'asap.staff.email-test.stale-status.2';
    const next = dom.window.sessionStorage.getItem(storageKey);
    oldRead.resolve({ code: 'ok', data: { id: accepted.data.id, organizationId: 2, status: 'sent', deliveryMode: 'postmark' } });
    await stale;
    assert.equal(dom.window.sessionStorage.getItem(storageKey), next, 'an old status cannot alter the new intent');
    assert.equal(root.querySelector('[data-email-test="send"]').disabled, true);
    assert.doesNotMatch(root.querySelector('[data-email-test="result"]').textContent, /email service accepted/);
    assert.equal(posts, 2);
    controller.dispose();
  });
  await recoveryFixture(async ({ load, get, dom, advance }) => {
    const { createEmailTestController } = await load('email-test');
    let value = { ownerKey: 'late-ack', organizationId: 2, active: true, ready: true, dirty: false, blocked: false };
    const root = get('#settings-email-test'), oldPost = deferred();
    let posts = 0;
    const controller = createEmailTestController({ root, context: () => value, request: async (path, init = {}) => {
      if (path.includes('/test-context')) return saved(value.organizationId);
      if (init.method === 'POST') { posts += 1; if (posts === 1) return oldPost.promise; throw new Error('Unrecorded new submission'); }
      throw notFound();
    } });
    controller.sync(); await settle();
    const submitting = controller.send();
    value = { ...value, organizationId: 3 }; controller.sync(); await settle();
    value = { ...value, organizationId: 2 }; controller.sync(); await settle();
    advance(60001); await controller.refresh();
    let confirmations = 0;
    require('./helpers/confirmation.cjs').answerConfirmations(dom.window, () => { confirmations += 1; return false; });
    root.querySelector('[data-email-test="retire"]').click(); await settle();
    assert.equal(confirmations, 1);
    assert.equal(root.querySelector('[data-email-test="send"]').disabled, true, 'canceling the warning preserves the pending intent');
    require('./helpers/confirmation.cjs').answerConfirmations(dom.window, () => true);
    root.querySelector('[data-email-test="retire"]').click(); await settle();
    root.querySelector('[data-email-test="send"]').click(); await settle();
    const storageKey = 'asap.staff.email-test.late-ack.2', next = dom.window.sessionStorage.getItem(storageKey);
    oldPost.resolve(accepted); await submitting;
    assert.equal(dom.window.sessionStorage.getItem(storageKey), next, 'an old POST acknowledgement cannot overwrite a newer intent');
    assert.equal(posts, 2);
    assert.equal(root.querySelector('[data-email-test="send"]').disabled, true);
    controller.dispose();
  });
  await recoveryFixture(async ({ load, get, dom, advance }) => {
    const { createEmailTestController } = await load('email-test');
    const value = { ownerKey: 'retain-failure', organizationId: 2, active: true, ready: true, dirty: false, blocked: false };
    const root = get('#settings-email-test');
    let posts = 0;
    const controller = createEmailTestController({ root, context: () => value, request: async (path, init = {}) => {
      if (path.includes('/test-context')) return saved(2);
      if (init.method === 'POST') { posts += 1; throw new Error('Never recorded'); }
      throw notFound();
    } });
    controller.sync(); await settle(); await controller.send();
    for (let index = 0; index < 3; index += 1) await controller.refresh();
    assert.equal(root.querySelector('[data-email-test="retire"]').hidden, true, 'multiple immediate 404s do not establish sufficient absence');
    advance(60001); await controller.refresh();
    const setItem = dom.window.Storage.prototype.setItem;
    dom.window.Storage.prototype.setItem = function (key, data) {
      if (key.endsWith('.retirement-references')) throw new Error('Storage unavailable');
      return setItem.call(this, key, data);
    };
    root.querySelector('[data-email-test="retire"]').click(); await settle();
    assert.match(root.querySelector('[data-email-test="result"]').textContent, /Could not retain/);
    assert.equal(root.querySelector('[data-email-test="send"]').disabled, true);
    assert.equal(JSON.parse(dom.window.sessionStorage.getItem('asap.staff.email-test.retain-failure.2')).pending, true);
    assert.equal(posts, 1);
    controller.dispose();
  });
  for (const duringFinalRead of [false, true]) {
    await recoveryFixture(async ({ load, get, dom, advance }) => {
      const { createEmailTestController } = await load('email-test');
      let firstActive = true, pause = false, acknowledged = false, posts = 0;
      const value = { ownerKey: 'two-panels', organizationId: 2, active: true, ready: true, dirty: false, blocked: false };
      const oldPost = deferred(), finalRead = deferred(), firstRoot = get('#settings-email-test'), secondRoot = get('#operations-email-test');
      const request = async (path, init = {}) => {
        if (path.includes('/test-context')) return saved(2);
        if (init.method === 'POST') { posts += 1; return oldPost.promise; }
        if (pause) return finalRead.promise;
        if (acknowledged) return { code: 'ok', data: { id: accepted.data.id, organizationId: 2,
          status: 'failed', lastErrorCode: 'provider_timeout', deliveryMode: 'postmark' } };
        throw notFound();
      };
      const first = createEmailTestController({ root: firstRoot, context: () => ({ ...value, active: firstActive }), request });
      const second = createEmailTestController({ root: secondRoot, context: () => value, request });
      first.sync(); await settle(); const submitting = first.send();
      firstActive = false; first.sync();
      second.sync(); await settle(); advance(60001); await second.refresh();
      pause = duringFinalRead; secondRoot.querySelector('[data-email-test="retire"]').click(); await settle();
      const storageKey = 'asap.staff.email-test.two-panels.2';
      if (!duringFinalRead) assert.equal(JSON.parse(dom.window.sessionStorage.getItem(storageKey)).retired, true);
      acknowledged = true; oldPost.resolve(accepted); await submitting;
      assert.equal(JSON.parse(dom.window.sessionStorage.getItem(storageKey)).id, accepted.data.id);
      if (duringFinalRead) { pause = false; finalRead.reject(notFound()); await settle(); }
      secondRoot.querySelector('[data-email-test="send"]').click(); await settle();
      assert.equal(posts, 1, 'a second panel must adopt a late known pending acknowledgement before authorizing Send');
      assert.equal(JSON.parse(dom.window.sessionStorage.getItem(storageKey)).pending, true);
      assert.equal(JSON.parse(dom.window.sessionStorage.getItem(storageKey)).id, accepted.data.id,
        'a final stale 404 cannot overwrite a durable reference learned by the other panel');
      await second.refresh();
      assert.match(secondRoot.querySelector('[data-email-test="result"]').textContent, /Acceptance is unconfirmed/);
      assert.equal(secondRoot.querySelector('[data-email-test="send"]').disabled, true);
      assert.equal(secondRoot.querySelector('[data-email-test="retire"]').hidden, true);
      first.dispose(); second.dispose();
    });
  }
  for (const missing of [null, '{invalid']) {
    await recoveryFixture(async ({ load, get, dom, advance }) => {
      const { createEmailTestController } = await load('email-test');
      const value = { ownerKey: 'lost-storage', organizationId: 2, active: true, ready: true, dirty: false, blocked: false };
      const root = get('#settings-email-test');
      let posts = 0;
      const controller = createEmailTestController({ root, context: () => value, request: async (path, init = {}) => {
        if (path.includes('/test-context')) return saved(2);
        if (init.method === 'POST') { posts += 1; throw new Error('Lost submission'); }
        throw notFound();
      } });
      controller.sync(); await settle(); await controller.send(); await controller.refresh();
      advance(60001); await controller.refresh();
      const key = 'asap.staff.email-test.lost-storage.2', original = dom.window.sessionStorage.getItem(key);
      if (missing === null) dom.window.sessionStorage.removeItem(key);
      else dom.window.sessionStorage.setItem(key, missing);
      controller.sync(); await settle(); await controller.send();
      assert.equal(root.querySelector('[data-email-test="send"]').disabled, true);
      assert.equal(root.querySelector('[data-email-test="retire"]').hidden, true);
      assert.match(root.querySelector('[data-email-test="result"]').textContent, /stored test reference is unavailable/);
      assert.equal(posts, 1, 'lost or malformed storage cannot resolve an in-memory uncertain intent');
      dom.window.sessionStorage.setItem(key, original); await controller.refresh();
      assert.equal(root.querySelector('[data-email-test="retire"]').hidden, true, 'restoring storage requires fresh absence evidence');
      controller.dispose();
    });
  }
  console.log('Shared saved-configuration email test controller lifecycle, no-replay and mode evidence passed.');
})().catch(error => { console.error(error); process.exitCode = 1; });
