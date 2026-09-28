const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const source = fs.readFileSync(path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend', 'staff', 'js', 'recent-requests.js'), 'utf8')
  .replace(/export function /g, 'function ');
const holder = {};
new Function('holder', `${source}\nholder.value = {
  recentStorageKey, validRequestId, readRecentRequests, rememberRecentRequest, forgetRecentRequest
};`)(holder);
const { recentStorageKey, validRequestId, readRecentRequests, rememberRecentRequest, forgetRecentRequest } = holder.value;

const data = new Map();
const storage = {
  getItem: key => data.get(key) ?? null,
  setItem: (key, value) => data.set(key, value)
};
const tenantId = '11111111-1111-1111-1111-111111111111';
const first = recentStorageKey({ tenantId, id: '9007199254740993', authenticationEmail: 'first@example.org' });
const second = recentStorageKey({ tenantId, id: '9007199254740994', authenticationEmail: 'second@example.org' });
const otherTenant = recentStorageKey({ tenantId: '22222222-2222-2222-2222-222222222222', id: '9007199254740993', authenticationEmail: 'first@example.org' });
const reboundAccount = recentStorageKey({ tenantId, id: '9007199254740993', authenticationEmail: 'replacement@example.org' });
assert.notEqual(first, second);
assert.notEqual(first, otherTenant);
assert.notEqual(first, reboundAccount);
assert.equal(validRequestId('9007199254740993'), true);
for (const value of ['0', '01', '1e3', '9223372036854775808', 9007199254740993, '<img>']) {
  assert.equal(validRequestId(value), false, String(value));
}

rememberRecentRequest(storage, first, '9007199254740993');
rememberRecentRequest(storage, first, '9223372036854775807');
assert.deepEqual(readRecentRequests(storage, first).map(item => item.id),
  ['9223372036854775807', '9007199254740993']);
assert.deepEqual(readRecentRequests(storage, second), [], 'Another staff identity must not inherit recents');
assert.deepEqual(readRecentRequests(storage, otherTenant), [], 'Another tenant must not inherit recents');
assert.equal(data.get(first).includes('title'), true);
assert.equal(data.get(first).includes('library'), false, 'Storage must not carry a scope hint');

data.set(first, JSON.stringify([
  { type: 'additional_copy', id: '9007199254740993' },
  { type: 'title_request', id: '<img src=x onerror=alert(1)>', title: '<script>' },
  { type: 'title_request', id: '9007199254740993', title: '<script>', libraryOrgId: 999 },
  { type: 'title_request', id: '9007199254740993' }
]));
assert.deepEqual(readRecentRequests(storage, first), [{ type: 'title_request', id: '9007199254740993' }]);
assert.deepEqual(forgetRecentRequest(storage, first, '9007199254740993'), []);
data.set(first, '{bad json');
assert.deepEqual(readRecentRequests(storage, first), []);

console.log('Staff recent navigation identity and storage checks passed');
