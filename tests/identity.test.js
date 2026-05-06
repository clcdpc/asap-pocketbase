const assert = require('assert');
const {
  buildIdentityKey,
  displayIdentity,
  normalizeDomain,
  normalizeUsername,
  parseAllowedStaffUsers,
  parseStaffIdentity
} = require('../lib/identity.js');

let totalPassed = 0;
let totalFailed = 0;

function runTestSuite(name, testCases, testFn) {
  console.log(`Running suite: ${name}`);
  let passed = 0;
  let failed = 0;

  testCases.forEach((tc, index) => {
    try {
      testFn(tc, index);
      console.log(`  ✅ Test case ${index} passed`);
      passed++;
      totalPassed++;
    } catch (err) {
      console.error(`  ❌ Test case ${index} failed: ${err.message}`);
      if (err.expected !== undefined || err.actual !== undefined) {
        console.error(`     Expected:`, err.expected);
        console.error(`     Actual:  `, err.actual);
      }
      failed++;
      totalFailed++;
    }
  });
  console.log(`Finished suite: ${name}. ${passed} passed, ${failed} failed.\n`);
}

// --- parseStaffIdentity Tests ---
const parseStaffIdentityCases = [
  // 1. DOMAIN\username format
  {
    input: 'LIBRARY\\jsmith',
    defaultDomain: 'DEFAULT',
    expected: {
      username: 'jsmith',
      domain: 'library',
      authDomain: 'LIBRARY',
      identityKey: 'library\\jsmith',
      display: 'LIBRARY\\jsmith'
    }
  },
  // 2. username@DOMAIN format
  {
    input: 'jsmith@LIBRARY',
    defaultDomain: 'DEFAULT',
    expected: {
      username: 'jsmith',
      domain: 'library',
      authDomain: 'LIBRARY',
      identityKey: 'library\\jsmith',
      display: 'LIBRARY\\jsmith'
    }
  },
  // 3. Username only with a default domain
  {
    input: 'jsmith',
    defaultDomain: 'DEFAULT',
    expected: {
      username: 'jsmith',
      domain: 'default',
      authDomain: 'DEFAULT',
      identityKey: 'default\\jsmith',
      display: 'DEFAULT\\jsmith'
    }
  },
  // 4. Username only without a default domain
  {
    input: 'jsmith',
    defaultDomain: '',
    expected: {
      username: 'jsmith',
      domain: '',
      authDomain: '',
      identityKey: 'jsmith',
      display: 'jsmith'
    }
  },
  // 5. Formatting with leading/trailing whitespaces to test .trim() behavior
  {
    input: '  LIBRARY\\jsmith  ',
    defaultDomain: ' DEFAULT ',
    expected: {
      username: 'jsmith',
      domain: 'library',
      authDomain: 'LIBRARY',
      identityKey: 'library\\jsmith',
      display: 'LIBRARY\\jsmith'
    }
  },
  {
    input: '  jsmith@LIBRARY  ',
    defaultDomain: ' DEFAULT ',
    expected: {
      username: 'jsmith',
      domain: 'library',
      authDomain: 'LIBRARY',
      identityKey: 'library\\jsmith',
      display: 'LIBRARY\\jsmith'
    }
  },
  // 6. Empty/null input strings
  {
    input: '',
    defaultDomain: 'DEFAULT',
    expected: {
      username: '',
      domain: 'default',
      authDomain: 'DEFAULT',
      identityKey: 'default\\',
      display: 'DEFAULT\\'
    }
  },
  {
    input: null,
    defaultDomain: 'DEFAULT',
    expected: {
      username: '',
      domain: 'default',
      authDomain: 'DEFAULT',
      identityKey: 'default\\',
      display: 'DEFAULT\\'
    }
  },
  {
    input: undefined,
    defaultDomain: undefined,
    expected: {
      username: '',
      domain: '',
      authDomain: '',
      identityKey: '',
      display: ''
    }
  }
];

runTestSuite('parseStaffIdentity', parseStaffIdentityCases, (tc) => {
  const actual = parseStaffIdentity(tc.input, tc.defaultDomain);
  assert.deepStrictEqual(actual, tc.expected);
});

// --- buildIdentityKey Tests ---
const buildIdentityKeyCases = [
  { domain: 'DOMAIN', username: 'user', expected: 'domain\\user' },
  { domain: '', username: 'user', expected: 'user' },
  { domain: '  Domain  ', username: '  User  ', expected: 'domain\\user' },
  { domain: null, username: 'user', expected: 'user' },
  { domain: 'domain', username: null, expected: 'domain\\' },
  { domain: null, username: null, expected: '' }
];

runTestSuite('buildIdentityKey', buildIdentityKeyCases, (tc) => {
  const actual = buildIdentityKey(tc.domain, tc.username);
  assert.strictEqual(actual, tc.expected);
});

// --- displayIdentity Tests ---
const displayIdentityCases = [
  { domain: 'domain', username: 'user', expected: 'DOMAIN\\user' },
  { domain: '', username: 'user', expected: 'user' },
  { domain: '  Domain  ', username: '  User  ', expected: 'DOMAIN\\user' },
  { domain: null, username: 'user', expected: 'user' },
  { domain: 'domain', username: null, expected: 'DOMAIN\\' },
  { domain: null, username: null, expected: '' },
  { domain: undefined, username: undefined, expected: '' },
  { domain: 'dOmAiN', username: 'UsErNaMe', expected: 'DOMAIN\\username' },
  { domain: 123, username: 456, expected: '123\\456' },
  { domain: '   ', username: '   ', expected: '' }
];

runTestSuite('displayIdentity', displayIdentityCases, (tc) => {
  const actual = displayIdentity(tc.domain, tc.username);
  assert.strictEqual(actual, tc.expected);
});

// --- normalizeUsername Tests ---
const normalizeUsernameCases = [
  { input: '  User  ', expected: 'user' },
  { input: 'USER', expected: 'user' },
  { input: '', expected: '' },
  { input: null, expected: '' },
  { input: undefined, expected: '' }
];

runTestSuite('normalizeUsername', normalizeUsernameCases, (tc) => {
  const actual = normalizeUsername(tc.input);
  assert.strictEqual(actual, tc.expected);
});

// --- normalizeDomain Tests ---
const normalizeDomainCases = [
  { input: '  Domain  ', expected: 'domain' },
  { input: 'DOMAIN', expected: 'domain' },
  { input: '', expected: '' },
  { input: null, expected: '' },
  { input: undefined, expected: '' }
];

runTestSuite('normalizeDomain', normalizeDomainCases, (tc) => {
  const actual = normalizeDomain(tc.input);
  assert.strictEqual(actual, tc.expected);
});

// --- parseAllowedStaffUsers Tests ---
const parseAllowedStaffUsersCases = [
  {
    input: 'domain\\user1, user2@domain, user3',
    defaultDomain: 'DEFAULT',
    expected: ['domain\\user1', 'domain\\user2', 'default\\user3']
  },
  {
    input: 'user1, user1, USER1',
    defaultDomain: 'DEFAULT',
    expected: ['default\\user1']
  },
  {
    input: 'domain\\user1, domain\\user1',
    defaultDomain: '',
    expected: ['domain\\user1']
  },
  {
    input: '',
    defaultDomain: 'DEFAULT',
    expected: []
  },
  {
    input: null,
    defaultDomain: 'DEFAULT',
    expected: []
  }
];

runTestSuite('parseAllowedStaffUsers', parseAllowedStaffUsersCases, (tc) => {
  const actual = parseAllowedStaffUsers(tc.input, tc.defaultDomain);
  assert.deepStrictEqual(actual, tc.expected);
});

// --- Final Results ---
console.log(`All tests finished: ${totalPassed} passed, ${totalFailed} failed.`);

if (totalFailed > 0) {
  process.exit(1);
}
