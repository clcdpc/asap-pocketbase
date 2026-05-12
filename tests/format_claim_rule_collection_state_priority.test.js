const assert = require('assert');
const fs = require('fs');
const path = require('path');

function extractFunction(source, name) {
  const signature = `export function ${name}`;
  const start = source.indexOf(signature);
  if (start < 0) throw new Error(`Function ${name} not found`);
  let idx = source.indexOf('{', start);
  let depth = 1;
  idx += 1;
  while (idx < source.length && depth > 0) {
    const ch = source[idx];
    if (ch === '{') depth += 1;
    if (ch === '}') depth -= 1;
    idx += 1;
  }
  return source.slice(start, idx).replace(`export function ${name}`, `function ${name}`);
}

const source = fs.readFileSync(path.join(__dirname, '../pb_public/staff/js/settings-formats.js'), 'utf8');
const fnSource = extractFunction(source, 'collectFormatClaimRules');

console.log('Running format-claim rule collection state priority tests...');

const currentFormatClaimRules = [{ format: 'book', staffUserId: 'super-admin-id' }];
const document = {
  querySelectorAll(selector) {
    if (selector !== '.format-setting-row') return [];
    return [{
      getAttribute(name) { return name === 'data-key' ? 'book' : ''; },
      querySelector(name) {
        if (name !== '.format-claim-staff-select') return null;
        return {
          value: '',
          options: [{ value: '' }, { value: 'lib-admin-id' }]
        };
      }
    }];
  }
};

const collectFormatClaimRules = new Function('currentFormatClaimRules', 'document', `${fnSource}; return collectFormatClaimRules;`)(currentFormatClaimRules, document);
const rules = collectFormatClaimRules();

assert.deepStrictEqual(rules, [{ format: 'book', staffUserId: 'super-admin-id' }]);

console.log('All format-claim rule collection state priority tests passed!');
