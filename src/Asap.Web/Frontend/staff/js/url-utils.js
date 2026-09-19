export const statusStages = Object.freeze([
  'suggestion',
  'outstanding_purchase',
  'pending_hold',
  'hold_placed',
  'additional_copies',
  'closed',
  'settings',
  'analytics'
]);

export const stageQueryMap = Object.freeze({
  submitted: 'suggestion',
  suggestion: 'suggestion',
  new: 'suggestion',
  purchased_waiting_for_bib: 'outstanding_purchase',
  outstanding_purchase: 'outstanding_purchase',
  pending_hold: 'pending_hold',
  hold_placed: 'hold_placed',
  additional_copies: 'additional_copies',
  closed: 'closed',
  settings: 'settings',
  operations: 'operations',
  analytics: 'analytics'
});

export const LEAP_BIB_PLACEHOLDER = '{{bibid}}';
export const LEAP_PATRON_PLACEHOLDER = '{{patron-id}}';
export const LEAP_PATRON_COMPATIBILITY_PLACEHOLDER = '{{patronId}}';

function validateLeapPattern(value, requiredPlaceholder, label, allowCompatibilityPlaceholder = false) {
  const pattern = value === null || value === undefined ? '' : String(value).trim();
  if (!pattern) return null;
  if (!/^https?:\/\//i.test(pattern)) {
    return `${label} must begin with http:// or https://.`;
  }
  if (!pattern.includes(requiredPlaceholder) &&
      !(allowCompatibilityPlaceholder && pattern.includes(LEAP_PATRON_COMPATIBILITY_PLACEHOLDER))) {
    return allowCompatibilityPlaceholder
      ? `${label} must include ${LEAP_PATRON_PLACEHOLDER} or ${LEAP_PATRON_COMPATIBILITY_PLACEHOLDER}.`
      : `${label} must include ${requiredPlaceholder}.`;
  }
  try {
    const candidate = pattern
      .split(LEAP_BIB_PLACEHOLDER).join('placeholder')
      .split(LEAP_PATRON_PLACEHOLDER).join('placeholder')
      .split(LEAP_PATRON_COMPATIBILITY_PLACEHOLDER).join('placeholder');
    const parsed = new URL(candidate);
    if (!['http:', 'https:'].includes(parsed.protocol) || !parsed.hostname ||
        parsed.hostname.includes('placeholder')) {
      return `${label} must be a valid HTTP(S) URL.`;
    }
  } catch {
    return `${label} must be a valid HTTP(S) URL.`;
  }
  return null;
}

export function validateLeapBibUrlPattern(value) {
  return validateLeapPattern(value, LEAP_BIB_PLACEHOLDER, 'Leap BIB URL pattern');
}

export function validateLeapPatronUrlPattern(value) {
  return validateLeapPattern(value, LEAP_PATRON_PLACEHOLDER, 'Leap patron URL pattern', true);
}

export function leapBibUrl(pattern, bibId) {
  if (bibId === null || bibId === undefined || !String(bibId).trim() ||
      validateLeapBibUrlPattern(pattern)) return '';
  return String(pattern).trim().split(LEAP_BIB_PLACEHOLDER).join(encodeURIComponent(String(bibId).trim()));
}

export function leapPatronUrl(pattern, patronId) {
  if (patronId === null || patronId === undefined || !String(patronId).trim() ||
      validateLeapPatronUrlPattern(pattern)) return '';
  const encoded = encodeURIComponent(String(patronId).trim());
  return String(pattern).trim()
    .split(LEAP_PATRON_PLACEHOLDER).join(encoded)
    .split(LEAP_PATRON_COMPATIBILITY_PLACEHOLDER).join(encoded);
}

function readUrl(href) {
  return new URL(href === undefined || href === null ? window.location.href : String(href));
}

function historyPath(url) {
  return `${url.pathname}${url.search}${url.hash}`;
}

export function requestedStatusFromUrl(href) {
  const params = readUrl(href).searchParams;
  const raw = String(params.get('stage') || params.get('status') || '').trim();
  return stageQueryMap[raw] || '';
}

export function requestedRequestIdFromUrl(href) {
  const value = readUrl(href).searchParams.get('request');
  return String(value || '').trim();
}

export function replaceRequestUrl(href, id, additionalCopy = false) {
  const url = readUrl(href);
  const normalizedId = id === null || id === undefined ? '' : String(id).trim();
  if (normalizedId) url.searchParams.set('request', normalizedId);
  else url.searchParams.delete('request');
  if (additionalCopy) url.searchParams.set('stage', 'additional_copies');
  return historyPath(url);
}

export function replaceStageUrl(href, stage) {
  const url = readUrl(href);
  url.searchParams.delete('request');
  if (stage) url.searchParams.set('stage', String(stage));
  else url.searchParams.delete('stage');
  return historyPath(url);
}

export function replaceRequestParameter(id, additionalCopy = false) {
  window.history.replaceState(null, '', replaceRequestUrl(window.location.href, id, additionalCopy));
}

export function replaceStageParameter(stage) {
  window.history.replaceState(null, '', replaceStageUrl(window.location.href, stage));
}
