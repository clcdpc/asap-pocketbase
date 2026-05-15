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
    if (ch === '}') {
      depth--;
      if (opened && depth === 0) return source.slice(start, i + 1);
    }
  }
  throw new Error(`Could not parse ${name}`);
}

const source = fs.readFileSync(path.resolve(__dirname, '../lib/staff/title_requests.js'), 'utf8');
const fnCode = [
  extractFunction(source, 'handleDuplicateBibRequest'),
  extractFunction(source, 'markDuplicateClose'),
  extractFunction(source, 'wouldCreateActiveDuplicate'),
].join('\n');

function makeRecord(fields) {
  const data = { ...fields };
  return {
    id: data.id || 'request-1',
    get: key => data[key],
    set: (key, value) => { data[key] = value; },
    _data: data,
  };
}

function load(env) {
  return new Function('env', `with (env) { ${fnCode}; return handleDuplicateBibRequest; }`)(env);
}

{
  const current = makeRecord({ id: 'current', barcode: '21868001586580', bibid: '4276564' });
  const existing = makeRecord({ id: 'other', title: 'Battle of the Arctic', status: 'pending_hold' });
  const calls = { tags: [], saves: 0 };
  const handleDuplicateBibRequest = load({
    records: {
      STATUS: { PENDING_HOLD: 'pending_hold', HOLD_PLACED: 'hold_placed', CLOSED: 'closed' },
      CLOSE_REASON: { DUPLICATE_HOLD: 'duplicate_hold' },
      addWorkflowTagForRequest: (app, record, tag) => calls.tags.push(tag),
      appendSystemNote: () => {},
      duplicateContext: (record, matchType) => ({ id: record.id, matchType, status: record.get('status') }),
    },
  });
  const e = {
    app: {
      findRecordsByFilter: () => [existing],
      save: () => { calls.saves++; },
    },
    json: (status, body) => ({ status, body }),
  };
  const context = {
    id: 'current',
    record: current,
    oldStatus: 'outstanding_purchase',
    isActiveHoldTarget: true,
    action: '',
    isDuplicateClose: false,
    data: {},
  };

  const response = handleDuplicateBibRequest(e, context, '4276564');

  assert.strictEqual(response.status, 409);
  assert.strictEqual(response.body.code, 'duplicate_open_request');
  assert.strictEqual(response.body.duplicate.id, 'other');
  assert.strictEqual(response.body.duplicate.matchType, 'bibid');
  assert.ok(response.body.message.includes('already has an open request'));
  assert.deepStrictEqual(calls.tags, ['Hold exists (same patron)']);
  assert.strictEqual(calls.saves, 1);
}

{
  const current = makeRecord({ id: 'current', barcode: '21868001586580', bibid: '4276564' });
  const existing = makeRecord({ id: 'other', title: 'Battle of the Arctic', status: 'pending_hold' });
  const calls = { notes: [] };
  const handleDuplicateBibRequest = load({
    records: {
      STATUS: { PENDING_HOLD: 'pending_hold', HOLD_PLACED: 'hold_placed', CLOSED: 'closed' },
      CLOSE_REASON: { DUPLICATE_HOLD: 'duplicate_hold' },
      addWorkflowTagForRequest: () => {},
      appendSystemNote: (record, note) => calls.notes.push(note),
      duplicateContext: () => ({}),
    },
  });
  const e = {
    app: {
      findRecordsByFilter: () => [existing],
      save: () => { throw new Error('should not save here'); },
    },
    json: () => { throw new Error('should not return a conflict'); },
  };
  const context = {
    id: 'current',
    record: current,
    oldStatus: 'outstanding_purchase',
    isActiveHoldTarget: true,
    action: 'closeDuplicate',
    isDuplicateClose: true,
    data: {},
  };

  const response = handleDuplicateBibRequest(e, context, '4276564');

  assert.strictEqual(response, null);
  assert.strictEqual(context.data.status, 'closed');
  assert.strictEqual(context.data.closeReason, 'duplicate_hold');
  assert.ok(calls.notes[0].includes('Closed as duplicate'));
}

console.log('Staff duplicate BIB conflict tests passed.');
