'use strict';

// These are inherited PocketBase controls observed in the complete axe diagnostic
// report. Keep the match at state/rule/target level so new .NET-port regressions fail.
const LEGACY_ACCESSIBILITY_BASELINE = Object.freeze([
  { state: 'polaris-search', ruleId: 'color-contrast', target: '#polaris-search-status' },
  { state: 'polaris-search', ruleId: 'color-contrast', target: '.polaris-warning' },
  { state: 'polaris-search', ruleId: 'color-contrast', target: '.polaris-search-result-actions > .btn-outline-success.btn-sm.btn' },
  { state: 'settings-staff-access', ruleId: 'color-contrast', targetEndsWith: '.staff-user-delete.btn-outline-danger.btn-sm' },
  { state: 'settings-staff-access', ruleId: 'select-name', targetEndsWith: '.staff-role-select' },
  { state: 'postmark-settings', ruleId: 'color-contrast', target: '#btn-reset-library-settings' },
  { state: 'postmark-settings', ruleId: 'color-contrast', target: '#btn-test-smtp' },
  { state: 'postmark-settings', ruleId: 'color-contrast', target: '#settings-save-detail' },
  { state: 'postmark-settings', ruleId: 'color-contrast', target: '.asap-toast' }
]);

function targetSelector(target) {
  return (Array.isArray(target) ? target : [target]).map(String).join(' > ');
}

function matchesTarget(entry, selector) {
  if (entry.target === selector) return true;
  if (entry.targetEndsWith && selector.endsWith(entry.targetEndsWith)) return true;
  return false;
}

function isBaselined(state, ruleId, target) {
  const selector = targetSelector(target);
  return LEGACY_ACCESSIBILITY_BASELINE.some(entry =>
    entry.state === state &&
    entry.ruleId === ruleId &&
    matchesTarget(entry, selector));
}

function unexpectedLegacyAccessibilityFindings(states) {
  const unexpected = [];
  for (const scanned of states) {
    for (const violation of scanned.accessibility || []) {
      const nodes = (violation.nodes || []).filter(node =>
        !isBaselined(scanned.state, violation.id, node.target));
      if (nodes.length > 0) {
        unexpected.push({
          viewport: scanned.viewport,
          state: scanned.state,
          id: violation.id,
          impact: violation.impact,
          targets: nodes.map(node => node.target)
        });
      }
    }
  }
  return unexpected;
}

module.exports = { LEGACY_ACCESSIBILITY_BASELINE, unexpectedLegacyAccessibilityFindings };
