import { authToken } from './state.js';
import { requestJson } from '../../shared/http.js';

export class SessionExpiredError extends Error {
  constructor(message, requestToken) {
    super(message);
    this.name = 'SessionExpiredError';
    this.status = 401;
    this.requestToken = requestToken || '';
  }
}

export function getApiUrl(path) {
  return window.location.origin + path;
}

export async function request(path, options = {}) {
  const requestToken = authToken;
  const headers = {};
  if (requestToken) headers.Authorization = 'Bearer ' + requestToken;

  try {
    return await requestJson(getApiUrl(path), {
      ...options,
      requireObjectResponse: true,
      headers: { ...headers, ...(options.headers || {}) }
    });
  } catch (err) {
    if (err.status === 401 && !path.endsWith('/login')) {
      throw new SessionExpiredError('Your session has expired. Please log in again.', requestToken);
    }
    throw err;
  }
}

export function loadPatronConfig(path) {
  return request(path);
}

function isPositiveInt32(value) {
  return Number.isInteger(value) && value > 0 && value <= 2147483647;
}

function isCanonicalPositiveInt64Id(value) {
  return typeof value === 'string' && /^[1-9]\d*$/.test(value) &&
    (value.length < 19 || value.length === 19 && value <= '9223372036854775807');
}

function isRecord(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function hasStringProperties(value, properties) {
  return isRecord(value) && properties.every(property => typeof value[property] === 'string');
}

function isStringMap(value) {
  return isRecord(value) && Object.values(value).every(item => typeof item === 'string');
}

function hasValidAdditionalField(value) {
  if (!hasStringProperties(value, ['id', 'key', 'type', 'label']) ||
      typeof value.enabled !== 'boolean' ||
      value.helpText !== null && typeof value.helpText !== 'string' ||
      !Array.isArray(value.options)) {
    return false;
  }

  return value.options.every(option =>
    hasStringProperties(option, ['id', 'label']) && typeof option.enabled === 'boolean');
}

function hasValidPatronConfiguration(value) {
  if (!hasStringProperties(value, [
    'pageTitle', 'barcodeLabel', 'pinLabel', 'loginPrompt', 'loginNote', 'suggestionFormNote',
    'noEmailMessage', 'successTitle', 'successMessage', 'alreadySubmittedMessage',
    'misconfiguredMessage', 'ebookMessage', 'eaudiobookMessage', 'commonAuthorsList',
    'commonAuthorsLabel', 'commonAuthorsHelp', 'commonAuthorsMessage', 'externalSearch1Label',
    'externalSearch1UrlTemplate', 'externalSearch2Label', 'externalSearch2UrlTemplate',
    'externalSearch3Label', 'externalSearch3UrlTemplate', 'externalSearch4Label',
    'externalSearch4UrlTemplate', 'logoUrl', 'logoAlt', 'systemNotEnabledMessage', 'library'
  ]) || !isStringMap(value.duplicateStatusLabels) || !isStringMap(value.formatLabels) ||
      !isRecord(value.formatRules) ||
      !Array.isArray(value.publicationOptions) || !value.publicationOptions.every(item => typeof item === 'string') ||
      !Array.isArray(value.availableFormats) || !value.availableFormats.every(item => typeof item === 'string') ||
      !Array.isArray(value.additionalFieldDefinitions) || !value.additionalFieldDefinitions.every(hasValidAdditionalField)) {
    return false;
  }

  return [
    'commonAuthorsEnabled', 'externalSearch1Enabled', 'externalSearch2Enabled',
    'externalSearch3Enabled', 'externalSearch4Enabled', 'allowPatronAutoholdOptOut', 'systemNotEnabled'
  ].every(property => typeof value[property] === 'boolean');
}

function hasValidPatronSession(data, requireToken) {
  const tokenIsValid = !requireToken ||
    typeof data?.token === 'string' && data.token.trim().length > 0;
  const selectedBranchIsValid = !Object.prototype.hasOwnProperty.call(data || {}, 'selectedPickupBranchId') ||
    data.selectedPickupBranchId === null || isPositiveInt32(data.selectedPickupBranchId);

  return tokenIsValid &&
    typeof data?.barcode === 'string' && data.barcode.trim().length > 0 &&
    Number.isInteger(data?.effectiveLibraryOrgId) && data.effectiveLibraryOrgId > 1 &&
    data.effectiveLibraryOrgId <= 2147483647 &&
    Array.isArray(data?.pickupBranches) &&
    data.pickupBranches.every(branch => branch && typeof branch === 'object' && !Array.isArray(branch) &&
      isPositiveInt32(branch.id) && typeof branch.label === 'string') &&
    selectedBranchIsValid &&
    hasValidPatronConfiguration(data?.ui_text);
}

export function loginPatron(payload) {
  return request('/api/asap/patron/login', {
    method: 'POST',
    validateResponse: data => {
      if (!hasValidPatronSession(data, true)) {
        throw new Error('Login response is incomplete.');
      }
    },
    body: payload
  });
}

export function submitSuggestion(payload) {
  return request('/api/asap/patron/suggestions', {
    method: 'POST',
    validateResponse: (data, response) => {
      if (response?.status !== 201 || !isCanonicalPositiveInt64Id(data?.id) ||
          typeof data?.successTitle !== 'string' || typeof data?.successMessage !== 'string') {
        throw new Error('Suggestion response is incomplete.');
      }
    },
    body: payload
  });
}

export function restorePatronSession() {
  return request('/api/asap/patron/session', {
    validateResponse: data => {
      if (!hasValidPatronSession(data, false)) {
        throw new Error('Current patron information is incomplete.');
      }
    }
  });
}

export function logoutPatron() {
  return request('/api/asap/patron/logout', {
    method: 'POST',
    allowNoContent: true
  });
}
