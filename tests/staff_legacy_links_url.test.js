const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const sourcePath = path.join(
  __dirname,
  '..',
  'src',
  'Asap.Web',
  'Frontend',
  'staff',
  'js',
  'url-utils.js'
);
const constPattern = new RegExp('ex' + 'port const ', 'g');
const functionPattern = new RegExp('ex' + 'port function ', 'g');
const source = fs.readFileSync(sourcePath, 'utf8')
  .replace(constPattern, 'const ')
  .replace(functionPattern, 'function ');
const holder = {};
new Function('holder', `${source}\nholder.result = {
  requestedStatusFromUrl,
  requestedRequestIdFromUrl,
  replaceRequestUrl,
  replaceStageUrl,
  statusStages,
  validateLeapBibUrlPattern,
  validateLeapPatronUrlPattern,
  leapBibUrl,
  leapPatronUrl
};`)(holder);
const {
  requestedStatusFromUrl,
  requestedRequestIdFromUrl,
  replaceRequestUrl,
  replaceStageUrl,
  statusStages,
  validateLeapBibUrlPattern,
  validateLeapPatronUrlPattern,
  leapBibUrl,
  leapPatronUrl
} = holder.result;

const origin = 'https://staff.example.test';
const url = (query) => `${origin}/staff/${query}`;

for (const [query, expected] of [
  ['?stage=submitted', 'suggestion'],
  ['?stage=new', 'suggestion'],
  ['?stage=purchased_waiting_for_bib', 'outstanding_purchase'],
  ['?stage=hold_placed', 'hold_placed'],
  ['?stage=unknown&status=closed', ''],
  ['?status=additional_copies', 'additional_copies'],
  ['?stage=operations', 'operations'],
  ['?status=operations', 'operations'],
  ['?status=analytics', 'analytics'],
  ['', '']
]) {
  assert.equal(requestedStatusFromUrl(url(query)), expected, query);
}

const bigId = '9007199254740993';
assert.equal(
  requestedRequestIdFromUrl(url(`?request=%20${bigId}%20`)),
  bigId,
  'request IDs must remain exact strings beyond Number.MAX_SAFE_INTEGER'
);

const sourceUrl = url('?stage=submitted&status=new&request=legacy%20title&scope=2#details');
const normalizedTitle = new URL(replaceRequestUrl(sourceUrl, bigId), origin);
assert.equal(normalizedTitle.searchParams.get('request'), bigId);
assert.equal(normalizedTitle.searchParams.get('stage'), 'submitted');
assert.equal(normalizedTitle.searchParams.get('status'), 'new');
assert.equal(normalizedTitle.searchParams.get('scope'), '2');
assert.equal(normalizedTitle.hash, '#details');

const normalizedCopy = new URL(replaceRequestUrl(sourceUrl, '42', true), origin);
assert.equal(normalizedCopy.searchParams.get('request'), '42');
assert.equal(normalizedCopy.searchParams.get('stage'), 'additional_copies');
assert.equal(normalizedCopy.searchParams.get('status'), 'new');
assert.equal(normalizedCopy.searchParams.get('scope'), '2');
assert.equal(normalizedCopy.hash, '#details');

const cleared = new URL(replaceRequestUrl(sourceUrl, null), origin);
assert.equal(cleared.searchParams.has('request'), false);
assert.equal(cleared.searchParams.get('stage'), 'submitted');
assert.equal(cleared.searchParams.get('status'), 'new');
assert.equal(cleared.hash, '#details');

const switched = new URL(replaceStageUrl(sourceUrl, 'analytics'), origin);
assert.equal(switched.searchParams.has('request'), false);
assert.equal(switched.searchParams.get('stage'), 'analytics');
assert.equal(switched.searchParams.get('status'), 'new');
assert.equal(switched.searchParams.get('scope'), '2');
assert.equal(switched.hash, '#details');

for (const stage of ['suggestion', 'outstanding_purchase', 'pending_hold', 'hold_placed', 'additional_copies', 'closed', 'settings', 'analytics']) {
  assert.ok(statusStages.includes(stage), `missing supported stage ${stage}`);
}

assert.equal(validateLeapBibUrlPattern('https://leap.example/bib/{{bibid}}'), null);
assert.notEqual(validateLeapBibUrlPattern('https://leap.example/bib/42'), null);
assert.notEqual(validateLeapBibUrlPattern('javascript:alert(1)/{{bibid}}'), null);
assert.equal(validateLeapPatronUrlPattern('https://leap.example/patron/{{patronId}}'), null);
assert.notEqual(validateLeapPatronUrlPattern('https://leap.example/patron/{{barcode}}'), null);
assert.equal(
  leapBibUrl('https://leap.example/bib/{{bibid}}/{{bibid}}', 'BIB/42 7'),
  'https://leap.example/bib/BIB%2F42%207/BIB%2F42%207'
);
assert.equal(
  leapPatronUrl('https://leap.example/patron/{{patron-id}}?again={{patronId}}', '9123'),
  'https://leap.example/patron/9123?again=9123'
);
assert.equal(leapBibUrl('https://leap.example/bib/{{bibid}}', ''), '');
assert.equal(leapPatronUrl('javascript:alert(1)/{{patron-id}}', '9123'), '');
assert.equal(leapPatronUrl('https://leap.example/patron/{{patron-id}}', null), '');

console.log('Staff legacy-link URL contract checks passed');
