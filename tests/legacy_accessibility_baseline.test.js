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
    '#polaris-additional-copy-action-9001-0'
  ])),
  []
);
assert.deepEqual(
  unexpectedLegacyAccessibilityFindings(state('settings-staff-access', 'select-name', [
    'tr[data-staff-id="42"] > .staff-role-cell > .staff-role-select'
  ])),
  [{
    viewport: 'desktop',
    state: 'settings-staff-access',
    id: 'select-name',
    impact: 'critical',
    targets: [['tr[data-staff-id="42"] > .staff-role-cell > .staff-role-select']]
  }],
  'A role selector must not be exempted solely because it shares a class with another control.'
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
assert.equal(
  unexpectedLegacyAccessibilityFindings(state('polaris-search', 'color-contrast', ['.btn-outline-success'])).length,
  1,
  'A second element sharing the inherited class must not be baselined.'
);
assert.equal(
  unexpectedLegacyAccessibilityFindings(state('polaris-search', 'color-contrast', ['.polaris-additional-copy-action'])).length,
  1,
  'The inherited class without its stable element identity must not be baselined.'
);
for (const [name, scanState, ruleId, target] of [
  ['Polaris warning class', 'polaris-search', 'color-contrast', '.polaris-warning'],
  ['Staff delete control class', 'settings-staff-access', 'color-contrast', '.staff-user-delete.btn-outline-danger.btn-sm'],
  ['Staff role selector class', 'settings-staff-access', 'select-name', '.staff-role-select'],
  ['toast class', 'postmark-settings', 'color-contrast', '.asap-toast']
]) {
  assert.equal(
    unexpectedLegacyAccessibilityFindings(state(scanState, ruleId, [target])).length,
    1,
    `${name} must not be baselined without a stable element identity.`
  );
}
assert.deepEqual(
  unexpectedLegacyAccessibilityFindings(state('postmark-settings', 'color-contrast', ['#settings-save-toast'])),
  [],
  'The one inherited settings save toast can be identified by its stable ID.'
);

console.log('legacy accessibility baseline tests passed');
