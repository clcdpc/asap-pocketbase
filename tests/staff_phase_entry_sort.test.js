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
    if (ch === '{') {
      depth++;
      opened = true;
    }
    if (ch === '}') {
      depth--;
      if (opened && depth === 0) return source.slice(start, i + 1);
    }
  }
  throw new Error(`Could not parse ${name}`);
}

const source = fs.readFileSync(path.resolve(__dirname, '../lib/staff_routes.js'), 'utf8');
const fnCode = [
  extractFunction(source, 'statusIdByCodeMap'),
  extractFunction(source, 'requestPhaseEntryFallback'),
  extractFunction(source, 'preloadPhaseEntryTimesForRequests'),
  extractFunction(source, 'preloadPhaseEntryTimesBatch'),
  extractFunction(source, 'sortableTime'),
  extractFunction(source, 'sortTitleRequestRowsByPhaseEntry'),
].join('\n');

function load(env) {
  return new Function('env', `with (env) { ${fnCode}; return { preloadPhaseEntryTimesForRequests, sortTitleRequestRowsByPhaseEntry, requestPhaseEntryFallback }; }`)(env);
}

function makeRecord(id, fields) {
  const data = { ...fields };
  return {
    id,
    get: key => data[key],
    set: (key, value) => { data[key] = value; },
    _data: data,
  };
}

const records = {
  STATUS: {
    SUGGESTION: 'suggestion',
  },
  normalizeStatus: value => String(value || '').trim(),
};

{
  const { preloadPhaseEntryTimesForRequests, sortTitleRequestRowsByPhaseEntry } = load({ records });
  const pendingRef = 'status-pending-hold';
  const closedRef = 'status-closed';
  const titleRequests = [
    makeRecord('old-created-newer-entry', {
      status: 'pending_hold',
      statusRef: pendingRef,
      created: '2026-05-01T10:00:00.000Z',
      updated: '2026-05-13T10:00:00.000Z',
    }),
    makeRecord('new-created-older-entry', {
      status: 'pending_hold',
      statusRef: pendingRef,
      created: '2026-05-10T10:00:00.000Z',
      updated: '2026-05-14T10:00:00.000Z',
    }),
    makeRecord('closed-without-event', {
      status: 'closed',
      statusRef: closedRef,
      created: '2026-05-09T10:00:00.000Z',
      updated: '2026-05-12T10:00:00.000Z',
    }),
  ];
  const events = [
    makeRecord('e1', {
      titleRequest: 'new-created-older-entry',
      toStatus: pendingRef,
      created: '2026-05-11T10:00:00.000Z',
    }),
    makeRecord('e2', {
      titleRequest: 'old-created-newer-entry',
      toStatus: pendingRef,
      created: '2026-05-13T09:00:00.000Z',
    }),
    makeRecord('e3', {
      titleRequest: 'old-created-newer-entry',
      toStatus: 'status-suggestion',
      created: '2026-05-12T09:00:00.000Z',
    }),
  ];
  const app = {
    findRecordsByFilter: (collection, filter, sort, limit, offset) => {
      assert.strictEqual(collection, 'title_request_events');
      assert.strictEqual(sort, '-created');
      return events
        .slice()
        .sort((a, b) => new Date(b.get('created')) - new Date(a.get('created')))
        .slice(offset, offset + limit);
    },
  };
  const cache = {};

  preloadPhaseEntryTimesForRequests(app, titleRequests, cache);

  assert.strictEqual(cache['old-created-newer-entry'], '2026-05-13T09:00:00.000Z');
  assert.strictEqual(cache['new-created-older-entry'], '2026-05-11T10:00:00.000Z');
  assert.strictEqual(cache['closed-without-event'], '2026-05-12T10:00:00.000Z');

  const rows = [
    { id: 'new-created-older-entry', status: 'pending_hold', phaseEnteredAt: cache['new-created-older-entry'], updated: '2026-05-14T10:00:00.000Z', created: '2026-05-10T10:00:00.000Z' },
    { id: 'old-created-newer-entry', status: 'pending_hold', phaseEnteredAt: cache['old-created-newer-entry'], updated: '2026-05-13T10:00:00.000Z', created: '2026-05-01T10:00:00.000Z' },
  ];

  sortTitleRequestRowsByPhaseEntry(rows);

  assert.deepStrictEqual(rows.map(row => row.id), [
    'old-created-newer-entry',
    'new-created-older-entry',
  ]);
}

{
  const { requestPhaseEntryFallback } = load({ records });
  const suggestion = makeRecord('suggestion', {
    status: 'suggestion',
    created: '2026-05-01T10:00:00.000Z',
    updated: '2026-05-14T10:00:00.000Z',
  });
  const pending = makeRecord('pending', {
    status: 'pending_hold',
    created: '2026-05-01T10:00:00.000Z',
    updated: '2026-05-14T10:00:00.000Z',
  });

  assert.strictEqual(requestPhaseEntryFallback(suggestion), '2026-05-01T10:00:00.000Z');
  assert.strictEqual(requestPhaseEntryFallback(pending), '2026-05-14T10:00:00.000Z');
}

console.log('Staff phase-entry sort tests passed.');
