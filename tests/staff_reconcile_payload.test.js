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

const source = fs.readFileSync(path.resolve(__dirname, '../lib/staff_routes.js'), 'utf8');
const fnCode = extractFunction(source, 'reconcileBibAction');

function makeRecord(initial) {
  const data = { ...initial };
  return {
    get: (key) => data[key],
    set: (key, value) => { data[key] = value; },
    _data: data,
  };
}

function load(env) {
  return new Function('env', `with (env) { ${fnCode}; return reconcileBibAction; }`)(env);
}

{
  const record = makeRecord({ title: 'Computer', author: 'Old Author' });
  const context = {
    isDuplicateClose: false,
    isClosingRequest: false,
    record,
    data: {
      title: 'QuickBooks desktop all-in-one (Computer)',
      author: 'Nelson, Stephen L., 1959- author.',
      selectedPolarisBibId: '4271674',
      selectedPolarisTitle: 'QuickBooks desktop all-in-one',
      selectedPolarisAuthor: 'Nelson, Stephen L., 1959- author.',
    },
  };
  const reconcileBibAction = load({
    polaris: {
      reconcileRecord: () => {},
    },
  });

  reconcileBibAction({}, context, {}, '4271674');

  assert.strictEqual(context.data.title, 'QuickBooks desktop all-in-one (Computer)');
  assert.strictEqual(context.data.author, 'Nelson, Stephen L., 1959- author.');
}

{
  const record = makeRecord({ title: 'Computer', author: 'Old Author' });
  const context = {
    isDuplicateClose: false,
    isClosingRequest: false,
    record,
    data: {
      title: 'QuickBooks desktop all-in-one (Computer)',
      author: 'Nelson, Stephen L., 1959- author.',
    },
  };
  const reconcileBibAction = load({
    polaris: {
      reconcileRecord: (app, staff, rec) => {
        rec.set('title', 'QuickBooks desktop all-in-one (Computer)');
        rec.set('author', 'Nelson, Stephen L., 1959- author. (Old Author)');
      },
    },
  });

  reconcileBibAction({}, context, {}, '4271674');

  assert.strictEqual(context.data.title, 'QuickBooks desktop all-in-one (Computer)');
  assert.strictEqual(context.data.author, 'Nelson, Stephen L., 1959- author. (Old Author)');
}

console.log('Staff reconcile payload tests passed.');
