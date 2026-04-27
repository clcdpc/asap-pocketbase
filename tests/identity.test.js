const assert = require('assert');
const { parseStaffIdentity } = require('../pb_hooks/lib/identity.js');

const testCases = [
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

console.log('Running tests for pb_hooks/lib/identity.js...');

let passed = 0;
let failed = 0;

testCases.forEach((tc, index) => {
  const actual = parseStaffIdentity(tc.input, tc.defaultDomain);

  try {
    assert.deepStrictEqual(actual, tc.expected, `Test case ${index} failed: input="${tc.input}", defaultDomain="${tc.defaultDomain}"`);
    console.log(`✅ Test case ${index} passed`);
    passed++;
  } catch (err) {
    console.error(`❌ ${err.message}`);
    console.error(`   Expected:`, tc.expected);
    console.error(`   Actual:  `, actual);
    failed++;
  }
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
