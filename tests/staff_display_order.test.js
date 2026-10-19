const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');

(async () => {
  await fixture(async ({ load }) => {
    const { byDisplayLabel } = await load('display-order');
    const items = [
      { id: '9223372036854775807', name: 'alpha' },
      { id: '2', name: 'Zebra' },
      { id: '9007199254740993', name: 'Alpha' },
      { id: '10', name: 'Library 10' },
      { id: '9', name: 'Library 2' },
      { id: '4', name: null, upn: 'Bravo@example.org' }
    ];
    const before = structuredClone(items);
    assert.deepEqual(byDisplayLabel(items, item => item.name || item.upn).map(item => item.id),
      ['9007199254740993', '9223372036854775807', '4', '9', '10', '2']);
    assert.deepEqual(items, before, 'presentation sorting never mutates an authoritative source snapshot');
    assert.deepEqual(byDisplayLabel([{ id: 10, name: 'A' }, { id: 2, name: 'a' }]).map(item => item.id), [2, 10]);
    assert.deepEqual(byDisplayLabel(null), []);
  });
  console.log('Display label ordering passed.');
})().catch(error => { console.error(error); process.exitCode = 1; });
