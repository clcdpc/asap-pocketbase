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

const source = fs.readFileSync(path.resolve(__dirname, '../lib/staff_routes.js'), 'utf8');
const fnCode = extractFunction(source, 'applyCatalogFoundWorkflow');

function makeRecord(fields) {
  const data = {
    notes: '',
    workflowTags: [],
    ...fields,
  };
  return {
    id: data.id || 'r1',
    get: key => data[key],
    set: (key, value) => { data[key] = value; },
    _data: data,
  };
}

function tagsFor(record) {
  return record._data.workflowTags || [];
}

function notesFor(record) {
  return String(record.get('notes') || '').split('\n').filter(Boolean);
}

function load(env) {
  return new Function('env', `with (env) { ${fnCode}; return applyCatalogFoundWorkflow; }`)(env);
}

{
  const record = makeRecord({
    id: 'r1',
    status: 'suggestion',
    bibid: '',
    workflowTags: ['Identifier number not found in system'],
  });
  const calls = { saves: 0, canonicalRefs: 0 };
  const app = {
    findRecordById: () => record,
    save: () => { calls.saves++; },
  };
  const records = {
    updateTitleRequest: (appArg, id, data) => {
      const updated = appArg.findRecordById('title_requests', id);
      updated.set('status', data.status);
      updated.set('bibid', data.bibid);
      return updated;
    },
    addWorkflowTagForRequest: (appArg, rec, tag) => {
      const conflicts = tag === 'Identifier found'
        ? ['Identifier number not found in system']
        : [];
      rec._data.workflowTags = tagsFor(rec).filter(existing => !conflicts.includes(existing));
      if (!rec._data.workflowTags.includes(tag)) {
        rec._data.workflowTags.push(tag);
      }
      return true;
    },
    appendSystemNote: (rec, note) => {
      const existing = String(rec.get('notes') || '').trim();
      rec.set('notes', existing ? existing + '\n' + note : note);
    },
    setCanonicalRefs: () => { calls.canonicalRefs++; },
  };
  const applyCatalogFoundWorkflow = load({ records });
  const data = {
    action: 'catalogFound',
    status: 'pending_hold',
    bibid: '4271674',
    selectedPolarisBibId: '4271674',
    selectedPolarisTitle: 'QuickBooks desktop all-in-one',
  };
  const staff = { get: () => 'wosborn' };

  const updated = records.updateTitleRequest(app, record.id, data, 'wosborn');
  applyCatalogFoundWorkflow(app, updated, data, staff);

  assert.strictEqual(updated.get('status'), 'pending_hold');
  assert.strictEqual(updated.get('bibid'), '4271674');
  assert.ok(tagsFor(updated).includes('Identifier found'));
  assert.ok(!tagsFor(updated).includes('Identifier number not found in system'));
  assert.ok(notesFor(updated).some(note => note.includes('hold placement')));
  assert.strictEqual(calls.canonicalRefs, 1);
  assert.strictEqual(calls.saves, 1);
}

{
  const record = makeRecord({
    id: 'r2',
    status: 'suggestion',
    workflowTags: ['Identifier number not found in system'],
  });
  const calls = { saves: 0, tags: 0 };
  const applyCatalogFoundWorkflow = load({
    records: {
      addWorkflowTagForRequest: () => { calls.tags++; },
      appendSystemNote: () => {},
      setCanonicalRefs: () => {},
    },
  });
  applyCatalogFoundWorkflow({ save: () => { calls.saves++; } }, record, {
    action: 'catalogFound',
    bibid: '',
  }, {});

  assert.strictEqual(calls.tags, 0);
  assert.strictEqual(calls.saves, 0);
}

console.log('Staff catalog-found workflow tests passed.');
