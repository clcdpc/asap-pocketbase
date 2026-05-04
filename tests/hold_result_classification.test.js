const assert = require('assert');

global.__hooks = __dirname + '/../pb_hooks';

const jobs = require('../lib/jobs.js');

function runTests() {
  let result = jobs.classifyPolarisHoldResult({ ok: false, statusValue: 29, payload: {} });
  assert.strictEqual(result.ok, true);
  assert.strictEqual(result.tag, 'Hold exists (same patron)');
  assert.ok(result.note.includes('existing duplicate hold request'));

  result = jobs.classifyPolarisHoldResult({ ok: false, statusValue: 6, payload: {} });
  assert.strictEqual(result.ok, true);
  assert.strictEqual(result.tag, 'Hold exists (same patron)');

  result = jobs.classifyPolarisHoldResult({ ok: false, statusValue: -4006, payload: {} });
  assert.strictEqual(result.ok, false);
  assert.strictEqual(result.tag, 'Hold failed: bib');

  result = jobs.classifyPolarisHoldResult({ ok: false, statusValue: -4022, payload: {} });
  assert.strictEqual(result.ok, false);
  assert.strictEqual(result.tag, 'Hold failed: pickup');

  result = jobs.classifyPolarisHoldResult({ ok: true, statusValue: 0, payload: {} });
  assert.strictEqual(result.ok, true);
  assert.strictEqual(result.tag, 'Hold placed');

  result = jobs.classifyPolarisHoldResult({ ok: false, statusValue: -9999, payload: { ErrorMessage: 'Nope' } });
  assert.strictEqual(result.ok, false);
  assert.strictEqual(result.tag, 'Hold failed');
  assert.strictEqual(result.note, 'Nope');

  console.log('hold result classification tests passed.');
}

runTests();
