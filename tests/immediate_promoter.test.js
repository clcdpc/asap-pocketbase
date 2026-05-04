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

const jobsSource = fs.readFileSync(path.resolve(__dirname, '../lib/jobs.js'), 'utf8');
const fnCode = extractFunction(jobsSource, 'promoteRequestNow');

function makeRecord(initial) {
  const data = { ...initial };
  return {
    id: initial.id || 'r1',
    get: (k) => data[k],
    set: (k, v) => { data[k] = v; },
    _data: data,
  };
}

function makeEnv(overrides = {}) {
  const calls = { save: 0, search: 0, reconcile: 0 };
  const env = {
    mapIsbnCheckSuggestion: (s) => s === 'found' ? 'Identifier number found' : 'Identifier number not found in system',
    flagMultiplePolarisMatches: () => {},
    evaluatePurchase: (app, staff, record, cache, result) => { result.promoted += 1; record.set('status', 'pending_hold'); app.save(record); },
    records: {
      STATUS: { OUTSTANDING_PURCHASE: 'outstanding_purchase', SUGGESTION: 'suggestion' },
      normalizeStatus: (s) => String(s || '').toLowerCase(),
      appendSystemNote: () => {},
      addWorkflowTagForRequest: () => true,
    },
    polaris: {
      searchBib: () => { calls.search++; return { status: 'found', bibId: '123' }; },
      reconcileRecord: () => { calls.reconcile++; },
    },
    POLARIS_TAG_FOUND: 'Identifier found',
    POLARIS_TAG_NOT_FOUND: 'Identifier number not found in system',
    ...overrides,
  };
  env.app = { save: () => { calls.save++; } };
  env.calls = calls;
  return env;
}

function loadPromote(env) {
  return new Function('env', `with (env) { ${fnCode}; return promoteRequestNow; }`)(env);
}

// enabled+match via suggestion path
{
  const env = makeEnv();
  const promote = loadPromote(env);
  const record = makeRecord({ status: 'suggestion', identifier: '978', bibid: '' });
  const result = promote(env.app, {}, record);
  assert.strictEqual(result.status, 'found');
  assert.strictEqual(record.get('status'), 'suggestion');
  assert.strictEqual(record.get('isbnCheckStatus'), 'found');
  assert.strictEqual(record.get('bibid'), '123');
}

// enabled+no match
{
  const env = makeEnv({ polaris: { searchBib: () => ({ status: 'not_found' }), reconcileRecord: () => {} } });
  const promote = loadPromote(env);
  const record = makeRecord({ status: 'suggestion', identifier: 'x' });
  const result = promote(env.app, {}, record);
  assert.strictEqual(result.status, 'not_found');
  assert.strictEqual(record.get('isbnCheckStatus'), 'not_found');
}

// existing request changed uses outstanding purchase logic
{
  const env = makeEnv();
  const promote = loadPromote(env);
  const record = makeRecord({ status: 'outstanding_purchase', identifier: '978' });
  const result = promote(env.app, {}, record);
  assert.strictEqual(result.promoted, true);
  assert.strictEqual(record.get('status'), 'pending_hold');
}

// unchanged / unsupported status returns skipped
{
  const env = makeEnv();
  const promote = loadPromote(env);
  const record = makeRecord({ status: 'closed', identifier: '978' });
  const result = promote(env.app, {}, record);
  assert.strictEqual(result.status, 'skipped');
}

// failure doesn't prevent save in caller path (simulated by throw)
{
  const env = makeEnv({ polaris: { searchBib: () => { throw new Error('boom'); }, reconcileRecord: () => {} } });
  const promote = loadPromote(env);
  const record = makeRecord({ status: 'suggestion', identifier: '978' });
  assert.throws(() => promote(env.app, {}, record), /boom/);
}

console.log('Immediate promoter tests passed.');
