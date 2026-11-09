const assert = require('node:assert/strict');
const fs = require('node:fs');
const { test } = require('node:test');
const { fixture } = require('./helpers/staff-controller.cjs');

test('controller fixtures use fresh modules and DOM realms and restore globals after failure', async t => {
  const names = ['window', 'document', 'Node', 'HTMLElement', 'URLSearchParams', 'FormData', 'Event', 'fetch'];
  const before = names.map(name => Object.getOwnPropertyDescriptor(global, name));
  const remove = fs.rmSync, removed = [];
  t.mock.method(fs, 'rmSync', (directory, options) => { remove(directory, options); removed.push(directory); });
  let firstModule, firstWindow;
  const failure = new Error('Disposable journey failure');
  await assert.rejects(fixture(async ({ load, dom, get }) => {
    firstModule = await load('session-identity');
    firstWindow = dom.window;
    assert.equal(global.Event, dom.window.Event);
    assert.equal(global.FormData, dom.window.FormData);
    assert.ok(get('#settings-form') instanceof global.HTMLElement);
    global.fetch = () => { throw new Error('Retired network double'); };
    throw failure;
  }), error => error === failure);
  assert.deepEqual(names.map(name => Object.getOwnPropertyDescriptor(global, name)), before);
  assert.equal(removed.length, 1);
  assert.equal(fs.existsSync(removed[0]), false, 'Failed fixture removes its module copies');
  await fixture(async ({ load, dom, get }) => {
    const second = await load('session-identity');
    assert.notEqual(firstModule.createSessionIdentity, second.createSessionIdentity);
    assert.notEqual(firstWindow, dom.window);
    assert.ok(get('#settings-form') instanceof dom.window.HTMLElement);
    assert.equal(get('#settings-form') instanceof firstWindow.HTMLElement, false);
  });
  assert.deepEqual(names.map(name => Object.getOwnPropertyDescriptor(global, name)), before);
  assert.equal(removed.length, 2);
  assert.equal(fs.existsSync(removed[1]), false);
});
