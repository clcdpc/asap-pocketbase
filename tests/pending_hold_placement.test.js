const assert = require('assert');
const fs = require('fs');
const path = require('path');

function extractFunction(source, name) {
  const marker = `function ${name}(`;
  const start = source.indexOf(marker);
  if (start < 0) throw new Error(`Could not find ${name}`);
  let depth = 0;
  let opened = false;
  for (let i = start; i < source.length; i++) {
    const ch = source[i];
    if (ch === '{') { depth++; opened = true; }
    if (ch === '}') { depth--; if (opened && depth === 0) return source.slice(start, i + 1); }
  }
  throw new Error(`Could not parse ${name}`);
}

const source = fs.readFileSync(path.resolve(__dirname, '../lib/jobs/hold_placement.js'), 'utf8');
const fnCode = [
  extractFunction(source, 'classifyPolarisHoldResult'),
  extractFunction(source, 'noteNoHoldableItems'),
  extractFunction(source, 'processPendingHolds')
].join('\n');

function makeRecord(initial) {
  const data = { ...initial };
  return {
    id: initial.id || 'r1',
    get: (k) => data[k],
    getBool: (k) => Boolean(data[k]),
    set: (k, v) => { data[k] = v; },
    _data: data,
  };
}

function makeEnv(record, polarisOverrides = {}) {
  const calls = { tags: [], notes: [], saves: 0, placeHold: 0, replyToHold: 0, placeHoldOptions: [] };
  const env = {
    processPagedQueue: (app, result, opts, each) => each(record),
    records: {
      STATUS: { PENDING_HOLD: 'pending_hold', HOLD_PLACED: 'hold_placed' },
      addWorkflowTagForRequest: (app, rec, tag) => { calls.tags.push(tag); return true; },
      appendSystemNote: (rec, note) => calls.notes.push(note),
      setCanonicalRefs: () => {},
      recordEvent: () => {},
    },
    polaris: {
      searchBib: () => ({ status: 'found', bibId: 'b1' }),
      lookupPatron: () => ({ PatronID: 'p1', PatronOrgID: '10', RequestPickupBranchID: '20' }),
      patronHasHoldForBib: () => false,
      getBibHoldings: () => [{ BibID: 'b1', ItemsTotal: 1, Holdable: true }],
      summarizeHoldability: (rows) => ({ hasHoldableItems: rows.some((row) => row.Holdable === true) }),
      placeHold: (staff, bibId, patronId, options) => { calls.placeHold++; calls.placeHoldOptions.push(options); return { ok: true, statusValue: 0, payload: {} }; },
      replyToHold: () => { calls.replyToHold++; },
      reconcileRecord: () => {},
      ...polarisOverrides,
    },
    mail: { holdPlaced: () => true, noteSkipped: () => {} },
  };
  env.app = {
    save: () => { calls.saves++; },
    logger: () => ({ warn: () => {}, error: () => {} }),
  };
  env.calls = calls;
  return env;
}

function load(env) {
  return new Function('env', `with (env) { ${fnCode}; return processPendingHolds; }`)(env);
}

{
  const record = makeRecord({ bibid: 'b1', barcode: '2900', autohold: true });
  const env = makeEnv(record, {
    getBibHoldings: () => [],
    summarizeHoldability: () => ({ hasHoldableItems: false }),
  });
  const result = { skipped: 0, holdsPlaced: 0, errors: 0 };
  load(env)(env.app, {}, result);
  assert.strictEqual(env.calls.placeHold, 0);
  assert.deepStrictEqual(env.calls.tags, ['No holdable items']);
  assert.ok(env.calls.notes[0].includes('no holdable items'));
  assert.strictEqual(result.skipped, 1);
}

{
  const record = makeRecord({ bibid: 'b1', barcode: '2900', autohold: true });
  const env = makeEnv(record, {
    patronHasHoldForBib: () => true,
  });
  const result = { skipped: 0, holdsPlaced: 0, errors: 0 };
  load(env)(env.app, {}, result);
  assert.strictEqual(env.calls.placeHold, 0);
  assert.deepStrictEqual(env.calls.tags, ['Hold exists (same patron)']);
  assert.strictEqual(result.skipped, 1);
}

{
  const record = makeRecord({ bibid: 'b1', barcode: '2900', autohold: true });
  const env = makeEnv(record, {
    placeHold: (staff, bibId, patronId, options) => {
      env.calls.placeHold++;
      env.calls.placeHoldOptions.push(options);
      return { ok: true, statusValue: 5, payload: { RequestGUID: 'rg1' } };
    },
  });
  const result = { skipped: 0, holdsPlaced: 0, errors: 0 };
  load(env)(env.app, {}, result);
  assert.strictEqual(env.calls.placeHold, 1);
  assert.strictEqual(env.calls.replyToHold, 1);
  assert.deepStrictEqual(env.calls.tags, ['Hold placed']);
  assert.strictEqual(record.get('status'), 'hold_placed');
  assert.strictEqual(result.holdsPlaced, 1);
}


{
  const record = makeRecord({ bibid: 'b1', barcode: '2900', autohold: true });
  const env = makeEnv(record, {
    lookupPatron: () => ({ PatronID: '123', PatronOrgID: '10', RequestPickupBranchID: '20' }),
  });
  const result = { skipped: 0, holdsPlaced: 0, errors: 0 };
  load(env)(env.app, {}, result);
  assert.strictEqual(env.calls.placeHold, 1);
  assert.deepStrictEqual(env.calls.placeHoldOptions[0], { pickupOrgId: '20', noAutoReply: true });
  assert.strictEqual(result.holdsPlaced, 1);
}

{
  const record = makeRecord({ bibid: 'b1', barcode: '2900', autohold: true });
  const env = makeEnv(record, {
    lookupPatron: () => ({ PatronID: '123', PatronOrgID: '10', RequestPickupBranchID: '' }),
  });
  const result = { skipped: 0, holdsPlaced: 0, errors: 0 };
  load(env)(env.app, {}, result);
  assert.strictEqual(env.calls.placeHold, 1);
  assert.deepStrictEqual(env.calls.placeHoldOptions[0], { pickupOrgId: '10', noAutoReply: true });
  assert.strictEqual(result.holdsPlaced, 1);
}

{
  const record = makeRecord({ bibid: 'b1', barcode: '2900', autohold: true });
  const env = makeEnv(record, {
    lookupPatron: () => ({ PatronID: '123', PatronOrgID: '', RequestPickupBranchID: '' }),
  });
  const result = { skipped: 0, holdsPlaced: 0, errors: 0 };
  load(env)(env.app, {}, result);
  assert.strictEqual(env.calls.placeHold, 0);
  assert.deepStrictEqual(env.calls.tags, ['Hold failed: pickup']);
  assert.ok(env.calls.notes[0].includes('RequestPickupBranchID or PatronOrgID'));
  assert.strictEqual(result.skipped, 1);
}

console.log('Pending hold placement tests passed.');
