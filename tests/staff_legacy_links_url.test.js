const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const staff = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend', 'staff', 'js');
const state = fs.readFileSync(path.join(staff, 'state.js'), 'utf8');
const urls = fs.readFileSync(path.join(staff, 'app', 'url-utils.js'), 'utf8');
const gridData = fs.readFileSync(path.join(staff, 'grid-data.js'), 'utf8');

for (const [alias, stage] of [
  ['submitted', 'suggestion'],
  ['new', 'suggestion'],
  ['purchased_waiting_for_bib', 'outstanding_purchase'],
  ['additional_copies', 'additional_copies']
]) {
  assert.ok(state.includes(`"${alias}":"${stage}"`), `missing ${alias} legacy stage alias`);
}
assert.match(urls, /String\(params\.get\('request'\) \|\| ''\)\.trim\(\)/,
  'request IDs should remain browser strings');
assert.match(urls, /searchParams\.set\('request', value\)/,
  'resolved request IDs should replace only the request query value');
assert.match(urls, /url\.pathname \+ url\.search \+ url\.hash/,
  'deep-link replacement should preserve supported navigation and the hash');
assert.match(gridData, /title-requests/);
assert.match(gridData, /additional-copies/);
assert.match(gridData, /replaceResolvedRequestId\(row\.id/);
assert.match(gridData, /linked request could not be resolved/);
assert.doesNotMatch(gridData, /searchParams\.delete\('request'\)/,
  'a resolved deep link should retain the canonical request ID');

console.log('Staff legacy-link URL contract checks passed');
