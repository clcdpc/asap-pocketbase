const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');
const settle = async () => { for (let index = 0; index < 10; index += 1) await new Promise(resolve => setImmediate(resolve)); };
const deferred = () => { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };
const fingerprint = 'A'.repeat(64);
const saved = organizationId => ({ code: 'ok', data: { organizationId, scopeName: `<Library ${organizationId}>`,
  fromAddress: 'saved@example.org', fromName: 'Saved sender', recipient: 'primary@example.org',
  deliveryMode: 'postmark', state: 'ready', blockingReason: null, configurationVersion: fingerprint } });
const accepted = { code: 'queued', data: { id: '9007199254740993', version: 'outbox-v1', dispatchDelayed: false } };

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
  console.log('Shared saved-configuration email test controller lifecycle, no-replay and mode evidence passed.');
})().catch(error => { console.error(error); process.exitCode = 1; });
