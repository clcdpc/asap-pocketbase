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
function controlledTimers(window) {
  let now = 0, sequence = 0;
  const timers = new Map();
  window.setTimeout = (callback, delay = 0) => {
    const id = ++sequence;
    timers.set(id, { callback, at: now + delay });
    return id;
  };
  window.clearTimeout = id => timers.delete(id);
  return { advance(milliseconds) {
    const target = now + milliseconds;
    while (true) {
      const next = [...timers].filter(([, timer]) => timer.at <= target).sort((left, right) => left[1].at - right[1].at)[0];
      if (!next) break;
      const [id, timer] = next;
      now = timer.at; timers.delete(id); timer.callback();
    }
    now = target;
  } };
}
const recoveryFixture = journey => fixture(async environment => {
  const originalNow = Date.now;
  let now = originalNow();
  Date.now = () => now;
  try { await journey({ ...environment, advance: milliseconds => { now += milliseconds; } }); }
  finally { Date.now = originalNow; }
});

(async () => {
  await fixture(async ({ load, get, dom }) => {
    const { createEmailTestController, emailTestStatusMessage } = await load('email-test');
    const clock = controlledTimers(dom.window);
    let value = { ownerKey: 'actor-a', organizationId: 2, active: true, ready: true, dirty: false, blocked: false };
    const calls = [], posts = [], pendingPost = deferred();
    const root = get('#settings-email-test');
    const button = root.querySelector('[data-email-test="send"]');
    const result = root.querySelector('[data-email-test="result"]');
    let status = { id: '9007199254740993', organizationId: 2, status: 'sent', deliveryMode: 'postmark', version: 'AAAAAAAAAAE=' };
    const controller = createEmailTestController({ root, context: () => value, request: async (path, init = {}) => {
      calls.push({ path, init });
      if (init.method === 'POST') { posts.push({ path, init }); return pendingPost.promise; }
      if (path.includes('/test-context')) return saved(Number(new URL(path, 'https://localhost').searchParams.get('organizationId')));
      return { code: 'ok', data: status };
    } });
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
    assert.match(result.textContent, /queued for processing.*Email operations/);
    assert.doesNotMatch(result.textContent, /delivered to|received in/i);
    assert.match(result.textContent, /9007199254740993/);
    assert.equal(posts.length, 1);
    assert.equal(button.disabled, false);
    assert.ok(calls.some(call => call.path === '/api/asap/staff/email-operations/9007199254740993?organizationId=2'));
    assert.equal(root.querySelector('[data-email-test="refresh"]'), null);
    assert.equal(root.querySelector('[data-email-test="retire"]'), null, 'Settings routes detailed recovery to Operations');
    clock.advance(5999);
    assert.match(result.textContent, /queued for processing.*Email operations/, 'the acknowledgement remains until its explicit expiry');
    clock.advance(1);
    assert.equal(result.hidden, true, 'the routine submission acknowledgement is brief');
    assert.equal(result.textContent, '');
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
      return { code: 'ok', data: { id: '9223372036854775807', organizationId: 2, status: 'sent', deliveryMode: 'postmark', version: 'AAAAAAAAAAE=' } };
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
  await fixture(async ({ load, get, dom }) => {
    const { createEmailTestController } = await load('email-test');
    let value = { ownerKey: 'bounded-observer', organizationId: 2, active: true, ready: true, dirty: false, blocked: false };
    const clock = controlledTimers(dom.window);
    const root = get('#settings-email-test'), paused = deferred();
    let posts = 0, observations = 0;
    const controller = createEmailTestController({ root, context: () => value, observationLimit: 0,
      request: async (path, init = {}) => {
        if (path.includes('/test-context')) return saved(2);
        if (init.method === 'POST') { posts += 1; return accepted; }
        observations += 1;
        if (observations === 2) return paused.promise;
        return { code: 'ok', data: { id: accepted.data.id, organizationId: 2,
          status: observations === 1 ? 'pending' : 'sent', deliveryMode: 'postmark', version: 'AAAAAAAAAAE=' } };
      } });
    controller.sync(); await settle(); await controller.send(); await settle();
    clock.advance(6000);
    assert.match(root.querySelector('[data-email-test="result"]').textContent, /queued for processing.*View email operations.*Reference/);
    assert.equal(root.querySelector('[data-email-test="send"]').disabled, true);
    clock.advance(60000); await settle();
    assert.equal(observations, 1, 'the automatic observer stops at its deadline without requesting another message');
    const manual = controller.refresh();
    value = { ...value, active: false }; controller.sync();
    paused.resolve({ code: 'ok', data: { id: accepted.data.id, organizationId: 2,
      status: 'sent', deliveryMode: 'postmark', version: 'AAAAAAAAAAE=' } });
    await manual;
    assert.doesNotMatch(root.textContent, /email service accepted/, 'panel exit retires an in-flight status reply');
    value = { ...value, active: true }; controller.sync(); await settle();
    assert.equal(root.querySelector('[data-email-test="result"]').hidden, true, 'restored routine success belongs in the log');
    clock.advance(60000); await settle();
    assert.equal(observations, 3, 'a terminal result stops automatic observation');
    assert.equal(posts, 1, 'manual status refresh and panel restoration never POST the accepted intent again');
    controller.dispose();
  });
  await recoveryFixture(async ({ load, get, dom, advance }) => {
    const { createEmailTestController } = await load('email-test');
    const value = { ownerKey: 'never-recorded', organizationId: 2, active: true, ready: true, dirty: false, blocked: false };
    const root = get('#operations-email-test'), posts = [], lookups = [];
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
          return { code: 'ok', data: { id: accepted.data.id, organizationId: 2, deliveryMode: 'postmark', version: 'AAAAAAAAAAE=', ...row } };
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
      const root = get('#operations-email-test');
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
      const root = get('#operations-email-test'), finalRead = deferred(), posts = [];
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
    const root = get('#operations-email-test'), oldRead = deferred();
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
    oldRead.resolve({ code: 'ok', data: { id: accepted.data.id, organizationId: 2, status: 'sent', deliveryMode: 'postmark', version: 'AAAAAAAAAAE=' } });
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
    const root = get('#operations-email-test'), oldPost = deferred();
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
    const root = get('#operations-email-test');
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
          status: 'failed', lastErrorCode: 'provider_timeout', deliveryMode: 'postmark', version: 'AAAAAAAAAAE=' } };
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
      const root = get('#operations-email-test');
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
  for (const storageUnavailable of [false, true]) {
    await fixture(async ({ load, get, dom }) => {
      const { createEmailTestController } = await load('email-test');
      const value = { ownerKey: 'navigation-recovery', organizationId: 2, active: true, ready: true };
      const root = get('#settings-email-test'), key = 'asap.staff.email-test.navigation-recovery.2';
      const navigations = [];
      const controller = createEmailTestController({ root, context: () => value,
        onOperations: (...args) => navigations.push(args), request: async (path, init = {}) => {
          if (path.includes('/test-context')) return saved(2);
          if (init.method === 'POST') throw new Error('Unknown acknowledgement');
          throw notFound();
        } });
      controller.sync(); await settle();
      assert.equal(root.querySelector('[data-email-test="operations"]').hidden, false);
      await controller.send();
      const original = JSON.parse(dom.window.sessionStorage.getItem(key));
      dom.window.sessionStorage.removeItem(key);
      if (storageUnavailable) dom.window.Storage.prototype.setItem = () => { throw new Error('Storage unavailable'); };
      root.querySelector('[data-email-test="operations"]').click(); await settle();
      assert.equal(navigations.length, storageUnavailable ? 0 : 1);
      assert.equal(root.querySelector('[data-email-test="send"]').disabled, true);
      if (storageUnavailable) {
        assert.match(root.querySelector('[data-email-test="result"]').textContent, /Keep this page open and restore session storage/);
      } else {
        assert.equal(JSON.parse(dom.window.sessionStorage.getItem(key)).operationId, original.operationId,
          'navigation preserves the original in-memory identity after storage loss');
        assert.equal(navigations[0][0], 2);
        assert.equal(navigations[0][1].ownerKey, value.ownerKey);
      }
      controller.dispose();
    });
  }
  for (const panel of ['settings', 'operations']) {
    for (const reason of ['recipient_domain_not_allowed', 'mail_not_configured']) {
      for (const order of ['context-first', 'status-first']) {
        await fixture(async ({ load, get, dom }) => {
          const { createEmailTestController } = await load('email-test');
          const root = get(`#${panel}-email-test`), contextRead = deferred(), statusRead = deferred();
          const value = { ownerKey: 'blocked-history', organizationId: 2, active: true, ready: true };
          const key = 'asap.staff.email-test.blocked-history.2';
          const operationId = '130e5f99-b59c-4e39-9718-bec1e7e5d94d';
          dom.window.sessionStorage.setItem(key, JSON.stringify({ operationId, id: accepted.data.id, status: 'sent', pending: false }));
          let initial = true, posts = 0, status = 'sent';
          const response = () => ({ code: 'ok', data: { id: accepted.data.id, organizationId: 2, status,
            lastErrorCode: status === 'failed' ? 'provider_timeout' : null, deliveryMode: 'postmark', version: 'AAAAAAAAAAE=' } });
          const controller = createEmailTestController({ root, context: () => value, request: async (path, init = {}) => {
            if (init.method === 'POST') posts += 1;
            if (path.includes('/test-context')) return contextRead.promise;
            if (initial) { initial = false; return statusRead.promise; }
            return response();
          } });
          controller.sync();
          const blocked = { ...saved(2), data: { ...saved(2).data, state: 'blocked', blockingReason: reason } };
          if (order === 'context-first') { contextRead.resolve(blocked); await settle(); statusRead.resolve(response()); }
          else { statusRead.resolve(response()); await settle(); contextRead.resolve(blocked); }
          await settle();
          const result = root.querySelector('[data-email-test="result"]');
          const guidance = reason === 'mail_not_configured' ? /Configure the System Postmark credential/ : /allowed-recipient policy/;
          assert.equal(result.hidden, false, `${panel}/${order}: completed history must not hide saved readiness guidance`);
          assert.match(result.textContent, guidance);
          assert.equal(root.querySelector('[data-email-test="send"]').disabled, true);
          assert.match(root.querySelector('[data-email-test="context"]').textContent, /Live email via Postmark/);
          status = 'failed'; await controller.refresh();
          assert.match(result.textContent, /Acceptance is unconfirmed/);
          assert.match(result.textContent, guidance, 'readiness and unresolved outcome guidance remain independently visible');
          const stored = JSON.parse(dom.window.sessionStorage.getItem(key));
          assert.equal(stored.operationId, operationId);
          assert.equal(stored.id, accepted.data.id);
          assert.equal(stored.pending, true);
          await controller.send(); assert.equal(posts, 0, 'blocked configuration never weakens the original intent guard');
          controller.dispose();
        });
      }
    }
    for (const outcome of [
      { status: 'pending', pending: true, message: /queued for processing/ },
      { status: 'sent', pending: false, message: /^$/ },
      { status: 'suppressed', suppressionReason: 'test_configuration_changed', pending: false, message: /not sent/ },
      { status: 'failed', lastErrorCode: 'recipient_invalid', pending: false, message: /could not complete/ },
      { status: 'failed', lastErrorCode: 'provider_timeout', pending: true, message: /Acceptance is unconfirmed/ }
    ]) {
      await fixture(async ({ load, get, dom }) => {
        const { createEmailTestController } = await load('email-test');
        const clock = controlledTimers(dom.window);
        const root = get(`#${panel}-email-test`), submissions = [];
        const value = { ownerKey: `outcome-${panel}`, organizationId: 2, active: true, ready: true };
        let posts = 0;
        const controller = createEmailTestController({ root, context: () => value,
          observationLimit: 0, onSubmitted: owner => submissions.push(owner),
          request: async (path, init = {}) => {
            if (path.includes('/test-context')) return saved(2);
            if (init.method === 'POST') { posts += 1; return accepted; }
            return { code: 'ok', data: { id: accepted.data.id, organizationId: 2, deliveryMode: 'postmark', version: 'AAAAAAAAAAE=', ...outcome } };
          } });
        controller.sync(); await settle(); await controller.send();
        await settle(); clock.advance(6000);
        assert.match(root.querySelector('[data-email-test="result"]').textContent, outcome.message);
        assert.equal(root.querySelector('[data-email-test="result"]').hidden, outcome.status === 'sent');
        assert.equal(root.querySelector('[data-email-test="send"]').disabled, outcome.pending);
        assert.equal(submissions.length, 1, 'every accepted submission requests a log refresh');
        assert.equal(submissions[0].ownerKey, value.ownerKey);
        await controller.refresh();
        assert.equal(posts, 1, 'status refresh never sends a second test');
        assert.equal(JSON.parse(dom.window.sessionStorage.getItem(`asap.staff.email-test.${value.ownerKey}.2`)).pending, outcome.pending);
        controller.dispose();
      });
    }
  }
  for (const change of ['replacement', 'stored-status', 'new-send', 'scope', 'account', 'deactivate', 'cancel']) {
    await fixture(async ({ load, get, dom }) => {
      const { createEmailTestController } = await load('email-test');
      controlledTimers(dom.window);
      let value = { ownerKey: 'observation-owner', organizationId: 2, active: true, ready: true };
      const key = 'asap.staff.email-test.observation-owner.2';
      const operationId = '130e5f99-b59c-4e39-9718-bec1e7e5d94d';
      const root = get('#operations-email-test');
      let id = accepted.data.id, posts = 0;
      dom.window.sessionStorage.setItem(key, JSON.stringify({ operationId, id, pending: false, status: 'sent' }));
      const controller = createEmailTestController({ root, context: () => value, request: async (path, init = {}) => {
        if (path.includes('/test-context')) return saved(value.organizationId);
        if (init.method === 'POST') { posts += 1; id = '9007199254740995'; return { ...accepted, data: { ...accepted.data, id } }; }
        return { code: 'ok', data: { id, organizationId: value.organizationId, status: 'sent', deliveryMode: 'postmark', version: 'AAAAAAAAAAE=' } };
      } });
      controller.sync(); await settle();
      const cancellation = new dom.window.AbortController();
      const current = await controller.refresh({ signal: cancellation.signal });
      assert.equal(current().id, accepted.data.id);
      if (change === 'replacement') {
        dom.window.sessionStorage.setItem(key, JSON.stringify({ operationId: '2d52da70-c805-4e99-ac52-b2f30cab4120', id: '9007199254740995', pending: true }));
      } else if (change === 'stored-status') {
        dom.window.sessionStorage.setItem(key, JSON.stringify({ operationId, id, pending: true, status: 'failed', lastErrorCode: 'provider_timeout' }));
      } else if (change === 'new-send') {
        await controller.send(); await settle();
        assert.notEqual(JSON.parse(dom.window.sessionStorage.getItem(key)).operationId, operationId);
      } else if (change === 'scope') {
        value = { ...value, organizationId: 3 }; controller.sync(); await settle();
      } else if (change === 'account') {
        value = { ...value, ownerKey: 'replacement-owner' }; controller.sync(); await settle();
      } else if (change === 'deactivate') {
        value = { ...value, active: false }; controller.sync();
      } else cancellation.abort();
      assert.equal(current(), null, `${change}: an old accessor cannot return a stale or replacement operation`);
      assert.equal(posts, change === 'new-send' ? 1 : 0);
      controller.dispose();
    });
  }
  for (const panel of ['settings', 'operations']) {
    for (const version of [undefined, 'unreadable', 'AAE=', 'AAAAAAAAAAJ=']) {
      await fixture(async ({ load, get, dom }) => {
        const { createEmailTestController } = await load('email-test');
        const value = { ownerKey: 'invalid-version', organizationId: 2, active: true, ready: true };
        const key = 'asap.staff.email-test.invalid-version.2';
        const operationId = '130e5f99-b59c-4e39-9718-bec1e7e5d94d';
        const original = JSON.stringify({ operationId, id: accepted.data.id, pending: true, status: 'pending' });
        dom.window.sessionStorage.setItem(key, original);
        const root = get(`#${panel}-email-test`);
        let currentVersion = version, posts = 0;
        const controller = createEmailTestController({ root, context: () => value, request: async (path, init = {}) => {
          if (path.includes('/test-context')) return saved(2);
          if (init.method === 'POST') posts += 1;
          return { code: 'ok', data: { id: accepted.data.id, organizationId: 2, status: 'sent', deliveryMode: 'postmark', version: currentVersion } };
        } });
        controller.sync(); await settle();
        const guarded = JSON.parse(dom.window.sessionStorage.getItem(key));
        assert.equal(guarded.operationId, operationId);
        assert.equal(guarded.id, accepted.data.id);
        assert.equal(guarded.status, 'pending', 'unreadable freshness cannot advance saved processing state');
        assert.equal(guarded.pending, true);
        assert.equal(guarded.version, undefined);
        assert.equal(guarded.unconfirmed, true, 'unreadable status retains a Send guard across restoration');
        assert.equal(root.querySelector('[data-email-test="send"]').disabled, true);
        assert.match(root.querySelector('[data-email-test="result"]').textContent, /outcome is unconfirmed/);
        assert.equal(controller.adopt(2, operationId, { id: accepted.data.id, organizationId: 2,
          status: 'sent', deliveryMode: 'postmark', version }), false, 'legacy exact recovery also requires readable freshness evidence');
        assert.equal(JSON.parse(dom.window.sessionStorage.getItem(key)).unconfirmed, true);
        assert.equal(await controller.refresh(), undefined, 'malformed observations cannot supply a presentation accessor');
        await controller.send(); assert.equal(posts, 0);
        currentVersion = 'AAAAAAAAAAI='; await controller.refresh();
        assert.equal(JSON.parse(dom.window.sessionStorage.getItem(key)).operationId, operationId);
        assert.equal(JSON.parse(dom.window.sessionStorage.getItem(key)).pending, false);
        controller.dispose();
      });
    }
  }
  for (const panel of ['settings', 'operations']) {
    await fixture(async ({ load, get, dom }) => {
      const { createEmailTestController } = await load('email-test');
      let value = { ownerKey: 'reconciling-owner', organizationId: 2, active: true, ready: true, reconciling: true };
      const root = get(`#${panel}-email-test`), key = 'asap.staff.email-test.reconciling-owner.2';
      const operationId = '130e5f99-b59c-4e39-9718-bec1e7e5d94d';
      dom.window.sessionStorage.setItem(key, JSON.stringify({ operationId, id: accepted.data.id, pending: false, status: 'failed' }));
      let reads = 0, posts = 0;
      const controller = createEmailTestController({ root, context: () => value, request: async (path, init = {}) => {
        if (path.includes('/test-context')) return saved(2);
        if (init.method === 'POST') posts += 1;
        reads += 1;
        return { code: 'ok', data: { id: accepted.data.id, organizationId: 2, status: 'failed',
          lastErrorCode: 'mail_not_configured', version: 'AAAAAAAAAAE=' } };
      } });
      controller.sync(); await settle();
      assert.ok(reads > 0, 'the submission guard does not cancel status reconciliation');
      assert.equal(root.querySelector('[data-email-test="send"]').disabled, true);
      assert.match(root.querySelector('[data-email-test="result"]').textContent, /Checking the current test and email log/);
      await controller.send(); root.querySelector('[data-email-test="send"]').dispatchEvent(new dom.window.Event('click'));
      assert.equal(posts, 0);
      assert.equal(JSON.parse(dom.window.sessionStorage.getItem(key)).operationId, operationId);
      value = { ...value, reconciling: false }; controller.sync();
      assert.equal(root.querySelector('[data-email-test="send"]').disabled, false, 'consumed reconciliation releases a resolved request');
      controller.dispose();
    });
  }
  for (const panel of ['settings', 'operations']) {
    for (const outcome of ['failed', 'malformed', 'older']) {
      await fixture(async ({ load, get, dom }) => {
        const { createEmailTestController } = await load('email-test');
        const value = { ownerKey: 'restored-terminal', organizationId: 2, active: true, ready: true };
        const key = 'asap.staff.email-test.restored-terminal.2';
        const operationId = '130e5f99-b59c-4e39-9718-bec1e7e5d94d';
        dom.window.sessionStorage.setItem(key, JSON.stringify({ operationId, id: accepted.data.id, status: 'failed',
          lastErrorCode: 'mail_not_configured', pending: false, version: 'AAAAAAAAAAI=' }));
        const root = get(`#${panel}-email-test`), send = root.querySelector('[data-email-test="send"]');
        const result = root.querySelector('[data-email-test="result"]');
        let read = deferred(), posts = 0;
        const options = { root, context: () => value, request: async (path, init = {}) => {
          if (path.includes('/test-context')) return saved(2);
          if (init.method === 'POST') posts += 1;
          return read.promise;
        } };
        let controller = createEmailTestController(options);
        controller.sync(); await settle();
        assert.equal(send.disabled, true, `${panel}: restoration blocks Send while its exact read is held`);
        assert.match(result.textContent, /Checking the current test/);
        await controller.send(); send.dispatchEvent(new dom.window.Event('click'));
        assert.equal(posts, 0);
        if (outcome === 'failed') read.reject(Object.assign(new Error('Unavailable'), { status: 503 }));
        else read.resolve({ code: 'ok', data: { id: accepted.data.id, organizationId: 2, status: 'sent',
          deliveryMode: 'postmark', version: outcome === 'older' ? 'AAAAAAAAAAE=' : 'unreadable' } });
        await settle();
        let stored = JSON.parse(dom.window.sessionStorage.getItem(key));
        assert.equal(stored.operationId, operationId);
        assert.equal(stored.status, 'failed');
        assert.equal(stored.version, 'AAAAAAAAAAI=');
        assert.equal(stored.pending, false, 'a lookup failure does not invent a processing result');
        assert.equal(stored.unconfirmed, true);
        assert.equal(send.disabled, true, `${outcome}: unreadable status must not release Send`);
        assert.match(result.textContent, /outcome is unconfirmed.*Another test remains blocked.*Reference/);
        await controller.send(); assert.equal(posts, 0);
        controller.dispose();
        read = deferred();
        controller = createEmailTestController(options); controller.sync(); await settle();
        assert.equal(send.disabled, true, 'the persisted unresolved guard survives controller recreation');
        read.reject(Object.assign(new Error('Still unavailable'), { status: 503 })); await settle();
        await controller.send(); send.dispatchEvent(new dom.window.Event('click'));
        assert.equal(posts, 0);
        assert.equal(JSON.parse(dom.window.sessionStorage.getItem(key)).operationId, operationId);
        read = deferred();
        const recovering = controller.refresh();
        read.resolve({ code: 'ok', data: { id: accepted.data.id, organizationId: 2, status: 'sent',
          deliveryMode: 'postmark', version: 'AAAAAAAAAAM=' } });
        await recovering;
        stored = JSON.parse(dom.window.sessionStorage.getItem(key));
        assert.equal(stored.unconfirmed, false, 'a fresh valid exact completion releases the unresolved guard');
        assert.equal(stored.operationId, operationId);
        assert.equal(send.disabled, false);
        assert.equal(result.hidden, true, 'confirmed routine success still belongs in the log');
        assert.equal(posts, 0);
        controller.dispose();
      });
    }
  }
  await fixture(async ({ load, get, dom }) => {
    const { createEmailTestController } = await load('email-test');
    const value = { ownerKey: 'cancelled-review', organizationId: 2, active: true, ready: true };
    const key = 'asap.staff.email-test.cancelled-review.2';
    dom.window.sessionStorage.setItem(key, JSON.stringify({ operationId: '130e5f99-b59c-4e39-9718-bec1e7e5d94d',
      id: accepted.data.id, pending: false, status: 'sent', version: 'AAAAAAAAAAE=' }));
    let held = null;
    const controller = createEmailTestController({ root: get('#settings-email-test'), context: () => value,
      request: async path => path.includes('/test-context') ? saved(2) : held ? held.promise
        : { code: 'ok', data: { id: accepted.data.id, organizationId: 2, status: 'sent', deliveryMode: 'postmark', version: 'AAAAAAAAAAE=' } } });
    controller.sync(); await settle();
    const before = dom.window.sessionStorage.getItem(key), cancellation = new dom.window.AbortController();
    held = deferred();
    const refresh = controller.refresh({ signal: cancellation.signal });
    cancellation.abort(); held.reject(Object.assign(new Error('Cancelled read'), { name: 'AbortError' }));
    await refresh;
    assert.equal(dom.window.sessionStorage.getItem(key), before, 'cancellation cannot persist an ordinary unconfirmed failure');
    assert.equal(get('#settings-email-test [data-email-test="send"]').disabled, false);
    controller.dispose();
  });
  for (const panel of ['settings', 'operations']) {
    await fixture(async ({ load, get, dom }) => {
      const { createEmailTestController } = await load('email-test');
      const value = { ownerKey: 'lost-terminal-storage', organizationId: 2, active: true, ready: true };
      const key = 'asap.staff.email-test.lost-terminal-storage.2';
      const operationId = '130e5f99-b59c-4e39-9718-bec1e7e5d94d';
      dom.window.sessionStorage.setItem(key, JSON.stringify({ operationId, id: accepted.data.id, pending: false,
        status: 'failed', lastErrorCode: 'mail_not_configured', version: 'AAAAAAAAAAE=' }));
      const root = get(`#${panel}-email-test`), navigations = [];
      let posts = 0;
      const controller = createEmailTestController({ root, context: () => value,
        onOperations: scope => navigations.push(scope), request: async (path, init = {}) => {
          if (path.includes('/test-context')) return saved(2);
          if (init.method === 'POST') posts += 1;
          throw Object.assign(new Error('Status unavailable'), { status: 503 });
        } });
      controller.sync(); await settle();
      assert.equal(JSON.parse(dom.window.sessionStorage.getItem(key)).unconfirmed, true,
        'a real failed exact lookup creates the unresolved guard');
      dom.window.sessionStorage.removeItem(key);
      controller.sync(); await controller.send();
      root.querySelector('[data-email-test="send"]').dispatchEvent(new dom.window.Event('click'));
      assert.equal(posts, 0, 'removing persisted status cannot resolve the in-memory guarded request');
      assert.equal(root.querySelector('[data-email-test="send"]').disabled, true);
      assert.match(root.querySelector('[data-email-test="result"]').textContent, /stored test reference is unavailable/);
      if (panel === 'settings') {
        root.querySelector('[data-email-test="operations"]').click(); await settle();
        const restored = JSON.parse(dom.window.sessionStorage.getItem(key));
        assert.equal(restored.operationId, operationId); assert.equal(restored.id, accepted.data.id);
        assert.equal(restored.pending, false); assert.equal(restored.unconfirmed, true);
        assert.deepEqual(navigations, [2], 'Settings retains the guarded terminal request before departure');
      }
      controller.dispose();
    });
  }
  for (const outcome of ['failed', 'valid', 'cancelled']) {
    await fixture(async ({ load, get, dom }) => {
      const { createEmailTestController } = await load('email-test');
      const value = { ownerKey: 'storage-before-observation', organizationId: 2, active: true, ready: true };
      const key = 'asap.staff.email-test.storage-before-observation.2';
      const operationId = '130e5f99-b59c-4e39-9718-bec1e7e5d94d';
      dom.window.sessionStorage.setItem(key, JSON.stringify({ operationId, id: accepted.data.id, pending: false,
        status: 'failed', lastErrorCode: 'mail_not_configured', version: 'AAAAAAAAAAE=' }));
      let held = deferred(), posts = 0;
      const request = async (path, init = {}) => {
        if (path.includes('/test-context')) return saved(2);
        if (init.method === 'POST') posts += 1;
        return held.promise;
      };
      const root = get('#settings-email-test'), navigations = [];
      let controller = createEmailTestController({ root, context: () => value, request,
        onOperations: scope => navigations.push(scope) });
      controller.sync(); await settle();
      const cancellation = new dom.window.AbortController();
      const observation = outcome === 'cancelled' ? controller.refresh({ signal: cancellation.signal }) : null;
      await settle();
      dom.window.sessionStorage.removeItem(key);
      if (outcome === 'failed') held.reject(Object.assign(new Error('Status unavailable'), { status: 503 }));
      else if (outcome === 'valid') held.resolve({ code: 'ok', data: { id: accepted.data.id, organizationId: 2,
        status: 'sent', deliveryMode: 'postmark', version: 'AAAAAAAAAAI=' } });
      else {
        cancellation.abort(); held.reject(Object.assign(new Error('Cancelled'), { name: 'AbortError' }));
        await observation;
        assert.doesNotMatch(root.querySelector('[data-email-test="result"]').textContent, /outcome is unconfirmed/,
          'cancellation alone does not become an ordinary status failure');
      }
      await settle();
      if (outcome !== 'cancelled') {
        assert.equal(root.querySelector('[data-email-test="send"]').disabled, true,
          'missing storage guards the original before a failed or valid observation can return early');
      }
      await controller.send(); root.querySelector('[data-email-test="send"]').dispatchEvent(new dom.window.Event('click'));
      assert.equal(posts, 0);
      assert.equal(root.querySelector('[data-email-test="send"]').disabled, true);
      assert.match(root.querySelector('[data-email-test="result"]').textContent, /stored test reference is unavailable/);
      root.querySelector('[data-email-test="operations"]').click(); await settle();
      let stored = JSON.parse(dom.window.sessionStorage.getItem(key));
      assert.deepEqual(navigations, [2]);
      assert.equal(stored.operationId, operationId); assert.equal(stored.id, accepted.data.id);
      assert.equal(stored.unconfirmed, true); assert.equal(stored.pending, false);
      assert.equal(stored.status, 'failed'); assert.equal(stored.version, 'AAAAAAAAAAE=');
      controller.dispose();
      held = deferred();
      controller = createEmailTestController({ root: get('#operations-email-test'), context: () => value, request });
      controller.sync(); await settle();
      assert.equal(get('#operations-email-test [data-email-test="send"]').disabled, true, 'the original guard survives restoration');
      held.resolve({ code: 'ok', data: { id: accepted.data.id, organizationId: 2, status: 'sent',
        deliveryMode: 'postmark', version: 'AAAAAAAAAAI=' } });
      await settle();
      stored = JSON.parse(dom.window.sessionStorage.getItem(key));
      assert.equal(stored.operationId, operationId); assert.equal(stored.unconfirmed, false);
      assert.equal(stored.status, 'sent'); assert.equal(stored.version, 'AAAAAAAAAAI=');
      assert.equal(get('#operations-email-test [data-email-test="send"]').disabled, false,
        'restored storage and a fresh valid completion recover ordinary new-send permission');
      assert.equal(posts, 0);
      controller.dispose();
    });
  }
  await fixture(async ({ load, get, dom }) => {
    const { createEmailTestController } = await load('email-test');
    const value = { ownerKey: 'adoption-review', organizationId: 2, active: true, ready: true };
    const key = 'asap.staff.email-test.adoption-review.2';
    const operationId = '130e5f99-b59c-4e39-9718-bec1e7e5d94d';
    const root = get('#operations-email-test');
    dom.window.sessionStorage.setItem(key, JSON.stringify({ operationId, id: accepted.data.id, status: 'failed',
      lastErrorCode: 'mail_not_configured', pending: false, unconfirmed: true, version: 'AAAAAAAAAAI=' }));
    const controller = createEmailTestController({ root, context: () => value, request: async path => {
      if (path.includes('/test-context')) return saved(2);
      throw Object.assign(new Error('Unavailable'), { status: 503 });
    } });
    controller.sync(); await settle();
    const row = { id: accepted.data.id, organizationId: 2, status: 'failed', lastErrorCode: 'mail_not_configured',
      deliveryMode: 'postmark', version: 'AAAAAAAAAAE=' };
    const before = dom.window.sessionStorage.getItem(key);
    assert.equal(controller.adopt(2, operationId, row), false, 'older adoption cannot clear a newer remembered guard');
    assert.equal(controller.adopt(2, operationId, { ...row, id: '9007199254740995', version: 'AAAAAAAAAAM=' }), false,
      'the original operation cannot change its acknowledged reference');
    assert.equal(controller.adopt(2, '2d52da70-c805-4e99-ac52-b2f30cab4120', { ...row, version: 'AAAAAAAAAAM=' }), false,
      'another operation cannot replace an unresolved remembered request');
    assert.equal(dom.window.sessionStorage.getItem(key), before);
    assert.equal(root.querySelector('[data-email-test="send"]').disabled, true);
    assert.equal(controller.adopt(2, operationId, { ...row, version: 'AAAAAAAAAAI=' }), true,
      'equal valid evidence for the original reference can confirm its result');
    assert.equal(JSON.parse(dom.window.sessionStorage.getItem(key)).unconfirmed, false);
    assert.equal(root.querySelector('[data-email-test="send"]').disabled, false);
    assert.equal(controller.adopt(2, operationId, { ...row, status: 'pending', version: 'AAAAAAAAAAM=' }), true);
    assert.equal(JSON.parse(dom.window.sessionStorage.getItem(key)).pending, true);
    assert.equal(root.querySelector('[data-email-test="send"]').disabled, true);
    controller.dispose();
  });
  console.log('Shared saved-configuration email test controller lifecycle, no-replay and mode evidence passed.');
})().catch(error => { console.error(error); process.exitCode = 1; });
