const assert = require('node:assert/strict');
const {
  LEGACY_ACCESSIBILITY_BASELINE,
  unexpectedLegacyAccessibilityFindings
} = require('./browser/legacy-accessibility-baseline.cjs');

function state(name, id, targets) {
  return [{
    viewport: 'desktop',
    state: name,
    accessibility: [{
      id,
      impact: id === 'select-name' ? 'critical' : 'serious',
      nodes: targets.map(target => ({ target: [target] }))
    }]
  }];
}

assert.ok(LEGACY_ACCESSIBILITY_BASELINE.length > 0, 'The inherited baseline must remain explicit and reviewable.');
assert.deepEqual(
  unexpectedLegacyAccessibilityFindings(state('polaris-search', 'color-contrast', [
    '#polaris-search-status',
    '.polaris-warning',
    '.btn-outline-success'
  ])),
  []
);
assert.deepEqual(
  unexpectedLegacyAccessibilityFindings(state('settings-staff-access', 'select-name', [
    'tr[data-staff-id="42"] > .staff-role-cell > .staff-role-select'
  ])),
  []
);
assert.deepEqual(unexpectedLegacyAccessibilityFindings([]), [], 'An improved legacy control must not fail.');
assert.equal(
  unexpectedLegacyAccessibilityFindings(state('polaris-search', 'color-contrast', ['#new-regression'])).length,
  1,
  'A new target under a baselined rule must fail.'
);
assert.equal(
  unexpectedLegacyAccessibilityFindings(state('queue-search-tabs', 'color-contrast', ['#polaris-search-status'])).length,
  1,
  'A baselined target in a different state must fail.'
);
assert.equal(
  unexpectedLegacyAccessibilityFindings(state('polaris-search', 'select-name', ['#polaris-search-status'])).length,
  1,
  'A different rule on a baselined target must fail.'
);

console.log('legacy accessibility baseline tests passed');
