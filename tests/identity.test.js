const assert = require('assert');
const identity = require('../pb_hooks/lib/identity.js');

console.log('Running tests for pb_hooks/lib/identity.js...');

let passed = 0;
let failed = 0;

function runTest(name, fn) {
  try {
    fn();
    console.log(`✅ ${name} passed`);
    passed++;
  } catch (err) {
    console.error(`❌ ${name} failed`);
    console.error(`   Error: ${err.message}`);
    failed++;
  }
}

// Tests for normalizeUsername
runTest('normalizeUsername', () => {
  assert.strictEqual(identity.normalizeUsername(' JohnDoe '), 'johndoe');
  assert.strictEqual(identity.normalizeUsername('JOHNDOE'), 'johndoe');
  assert.strictEqual(identity.normalizeUsername('johndoe'), 'johndoe');
  assert.strictEqual(identity.normalizeUsername(null), '');
  assert.strictEqual(identity.normalizeUsername(undefined), '');
  assert.strictEqual(identity.normalizeUsername(''), '');
});

// Tests for normalizeDomain
runTest('normalizeDomain', () => {
  assert.strictEqual(identity.normalizeDomain(' MyDomain '), 'mydomain');
  assert.strictEqual(identity.normalizeDomain('MYDOMAIN'), 'mydomain');
  assert.strictEqual(identity.normalizeDomain('mydomain'), 'mydomain');
  assert.strictEqual(identity.normalizeDomain(null), '');
  assert.strictEqual(identity.normalizeDomain(undefined), '');
  assert.strictEqual(identity.normalizeDomain(''), '');
});

// Tests for buildIdentityKey
runTest('buildIdentityKey', () => {
  assert.strictEqual(identity.buildIdentityKey('DOMAIN', 'User'), 'domain\\user');
  assert.strictEqual(identity.buildIdentityKey('', 'User'), 'user');
  assert.strictEqual(identity.buildIdentityKey(null, 'User'), 'user');
  assert.strictEqual(identity.buildIdentityKey('domain', ''), 'domain\\');
});

// Tests for displayIdentity
runTest('displayIdentity', () => {
  assert.strictEqual(identity.displayIdentity('domain', 'User'), 'DOMAIN\\user');
  assert.strictEqual(identity.displayIdentity('', 'User'), 'user');
  assert.strictEqual(identity.displayIdentity(null, 'User'), 'user');
  assert.strictEqual(identity.displayIdentity('domain', ''), 'DOMAIN\\');
});

// Tests for parseStaffIdentity
runTest('parseStaffIdentity', () => {
  // Test DOMAIN\username format
  let res1 = identity.parseStaffIdentity('MYDOMAIN\\JohnDoe', 'default');
  assert.deepStrictEqual(res1, {
    username: 'johndoe',
    domain: 'mydomain',
    authDomain: 'MYDOMAIN',
    identityKey: 'mydomain\\johndoe',
    display: 'MYDOMAIN\\johndoe'
  });

  // Test username@domain format
  let res2 = identity.parseStaffIdentity('JohnDoe@MYDOMAIN', 'default');
  assert.deepStrictEqual(res2, {
    username: 'johndoe',
    domain: 'mydomain',
    authDomain: 'MYDOMAIN',
    identityKey: 'mydomain\\johndoe',
    display: 'MYDOMAIN\\johndoe'
  });

  // Test bare username with default domain
  let res3 = identity.parseStaffIdentity('JohnDoe', 'DEFAULT');
  assert.deepStrictEqual(res3, {
    username: 'johndoe',
    domain: 'default',
    authDomain: 'DEFAULT',
    identityKey: 'default\\johndoe',
    display: 'DEFAULT\\johndoe'
  });

  // Test bare username with no default domain
  let res4 = identity.parseStaffIdentity('JohnDoe', '');
  assert.deepStrictEqual(res4, {
    username: 'johndoe',
    domain: '',
    authDomain: '',
    identityKey: 'johndoe',
    display: 'johndoe'
  });

  // Test edge cases: empty strings
  let res5 = identity.parseStaffIdentity('', 'default');
  assert.deepStrictEqual(res5, {
    username: '',
    domain: 'default',
    authDomain: 'default',
    identityKey: 'default\\',
    display: 'DEFAULT\\'
  });

  let res6 = identity.parseStaffIdentity(null, null);
  assert.deepStrictEqual(res6, {
    username: '',
    domain: '',
    authDomain: '',
    identityKey: '',
    display: ''
  });

  // Test padding whitespace
  let res7 = identity.parseStaffIdentity('  DOMAIN\\User  ', 'default');
  assert.deepStrictEqual(res7, {
    username: 'user',
    domain: 'domain',
    authDomain: 'DOMAIN',
    identityKey: 'domain\\user',
    display: 'DOMAIN\\user'
  });
});

// Tests for parseAllowedStaffUsers
runTest('parseAllowedStaffUsers', () => {
  // Typical comma-separated list
  let res1 = identity.parseAllowedStaffUsers('DOMAIN\\User1, User2@DOMAIN, User3', 'DOMAIN');
  assert.deepStrictEqual(res1, [
    'domain\\user1',
    'domain\\user2',
    'domain\\user3'
  ]);

  // Duplicate elimination across different formats
  let res2 = identity.parseAllowedStaffUsers('DOMAIN\\User1, User1@DOMAIN, User1', 'DOMAIN');
  assert.deepStrictEqual(res2, [
    'domain\\user1'
  ]);

  // Empty values and malformed entries (empty username)
  let res3 = identity.parseAllowedStaffUsers('DOMAIN\\User1, , DOMAIN\\', 'DOMAIN');
  assert.deepStrictEqual(res3, [
    'domain\\user1'
  ]);

  // No default domain
  let res4 = identity.parseAllowedStaffUsers('User1, User2', '');
  assert.deepStrictEqual(res4, [
    'user1',
    'user2'
  ]);

  // Null/empty input
  assert.deepStrictEqual(identity.parseAllowedStaffUsers(null, 'default'), []);
  assert.deepStrictEqual(identity.parseAllowedStaffUsers('', 'default'), []);
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
