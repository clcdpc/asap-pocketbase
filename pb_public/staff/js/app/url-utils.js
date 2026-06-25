import { leapBibUrlPattern, leapPatronUrlPattern, statusStages, stageQueryMap } from '../state.js';

export function validateStaffUrl(value) {
  const text = String(value || '').trim();
  if (!text) return 'Staff URL is required.';
  let parsed;
  try {
    parsed = new URL(text);
  } catch (err) {
    return 'Enter a valid Staff URL beginning with http:// or https://.';
  }
  if (!['http:', 'https:'].includes(parsed.protocol)) {
    return 'Staff URL must start with http:// or https://.';
  }
  return null;
}

export function normalizeStaffUrl(value) {
  const url = new URL(String(value || '').trim());
  url.hash = '';
  if (url.pathname.replace(/\/+$/, '').toLowerCase() === '/staff') {
    url.pathname = '/staff/';
  }
  return url.toString();
}

export function normalizeLeapBibUrlPattern(value) {
  const text = String(value || '').trim();
  if (!text) return '';
  if (!/^https?:\/\//i.test(text)) {
    throw new Error('Leap Bib URL pattern must begin with http:// or https://.');
  }
  if (!text.includes('{{bibid}}')) {
    throw new Error('Leap Bib URL pattern must include {{bibid}}.');
  }
  return text;
}

export function normalizeLeapPatronUrlPattern(value) {
  const text = String(value || '').trim();
  if (!text) return '';
  if (!/^https?:\/\//i.test(text)) {
    throw new Error('Leap Patron URL pattern must begin with http:// or https://.');
  }
  if (!text.includes('{{patron-id}}') && !text.includes('{{patronId}}')) {
    throw new Error('Leap Patron URL pattern must include {{patron-id}}.');
  }
  return text;
}

export function leapBibUrl(bibId) {
  const pattern = String(leapBibUrlPattern || '').trim();
  const cleanBibId = String(bibId || '').trim();
  if (!pattern || !cleanBibId || !pattern.includes('{{bibid}}')) {
    return '';
  }
  if (!/^https?:\/\//i.test(pattern)) {
    return '';
  }
  return pattern.split('{{bibid}}').join(encodeURIComponent(cleanBibId));
}

export function leapPatronUrl(patronId) {
  const pattern = String(leapPatronUrlPattern || '').trim();
  const cleanPatronId = String(patronId || '').trim();
  if (!pattern || !cleanPatronId || (!pattern.includes('{{patron-id}}') && !pattern.includes('{{patronId}}'))) {
    return '';
  }
  if (!/^https?:\/\//i.test(pattern)) {
    return '';
  }
  return pattern
    .split('{{patron-id}}').join(encodeURIComponent(cleanPatronId))
    .split('{{patronId}}').join(encodeURIComponent(cleanPatronId));
}

export function requestedStatusFromUrl() {
  try {
    const params = new URLSearchParams(window.location.search || '');
    const raw = String(params.get('stage') || params.get('status') || '').trim();
    return stageQueryMap[raw] || '';
  } catch (err) {
    return '';
  }
}

export function requestedRequestIdFromUrl() {
  try {
    const params = new URLSearchParams(window.location.search || '');
    return String(params.get('request') || '').trim();
  } catch (err) {
    return '';
  }
}

export function updateStageQuery(status) {
  try {
    const url = new URL(window.location.href);
    if (statusStages.includes(status)) {
      url.searchParams.set('stage', status === 'suggestion' ? 'submitted' : status);
    }
    if (status !== 'settings' && url.hash.startsWith('#settings-')) {
      url.hash = '';
    }
    window.history.replaceState(null, '', url.pathname + url.search + url.hash);
  } catch (err) {}
}
