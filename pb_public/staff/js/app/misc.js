import { currentRejectionTemplates, workflowSettings, organizationsStatus, setOrganizationsStatus, setOrganizationsStatusMessage, organizationsStatusMessage } from '../state.js';
import { escapeAttr } from '../grid-utils.js';
import { requestJson } from '../../shared/http.js';
import { getFieldValue, getFieldChecked, setFieldChecked, setFieldValue, setInlineResult } from './dom.js';

export function isPocketBaseAutoCancelError(err) {
  const message = String((err && err.message) || err || '');
  return !!(err && err.status === 0 && /aborted|auto.?cancel/i.test(message));
}

export function isValidSmtpHost(host) {
  const value = String(host || '').trim();
  if (!value) return false;
  if (value.toLowerCase() === 'localhost') return true;
  if (value.includes('://') || value.includes('/') || value.includes(':') || /\s/.test(value)) return false;
  const ipv4Pattern = /^(25[0-5]|2[0-4]\d|1?\d?\d)(\.(25[0-5]|2[0-4]\d|1?\d?\d)){3}$/;
  if (ipv4Pattern.test(value)) return true;
  const labels = value.split('.');
  if (labels.length < 2) return false;
  return labels.every(label => /^[a-z0-9-]{1,63}$/i.test(label) && !label.startsWith('-') && !label.endsWith('-'));
}

export function validateSmtpHostField(showMessage = false) {
  const host = getFieldValue('smtp-host').trim();
  const resultEl = document.getElementById('smtp-test-result');
  if (!host || isValidSmtpHost(host)) {
    if (showMessage && resultEl) {
      resultEl.textContent = '';
      resultEl.className = 'd-block mt-2';
    }
    return true;
  }
  if (showMessage && resultEl) {
    resultEl.textContent = 'Enter a valid SMTP host (DNS name or IP only, no protocol or port).';
    resultEl.className = 'mt-2 text-danger font-weight-bold small';
  }
  return false;
}

export function updateOrganizationsStatusUi(status, message) {
  setOrganizationsStatus(status || 'not_loaded');
  setOrganizationsStatusMessage(message || '');
  const statusEl = document.getElementById('organizations-status-message');
  const container = document.getElementById('enabled-libraries-checkbox-container');
  if (container && organizationsStatus !== 'loaded') {
    container.removeAttribute('data-loaded');
  }

  if (statusEl) {
    const classMap = {
      not_loaded: 'alert alert-info small mb-3',
      loading: 'alert alert-info small mb-3',
      loaded: 'alert alert-success small mb-3',
      error: 'alert alert-warning small mb-3'
    };
    statusEl.className = classMap[organizationsStatus] || classMap.not_loaded;
    statusEl.textContent = organizationsStatusMessage || 'Polaris organization sync status is unknown.';
  }

  if (container) {
    container.replaceChildren();
    const div = document.createElement('div');
    div.className = 'p-3';
    if (organizationsStatus === 'loading') {
      div.classList.add('text-muted');
      div.textContent = 'Organizations loading...';
    } else if (organizationsStatus === 'error') {
      div.classList.add('text-warning');
      div.textContent = 'Polaris connected, but organizations could not be loaded. Some setup options may be unavailable until this sync succeeds.';
    } else if (organizationsStatus === 'not_loaded') {
      div.classList.add('text-muted');
      div.textContent = 'Organizations not loaded yet.';
    } else {
      return;
    }
    container.appendChild(div);
  }
}

export async function postPolarisTest(url, resultEl, payload, options = {}) {
  const btn = options.button || null;
  if (btn) btn.disabled = true;
  setInlineResult(resultEl, options.pendingText || 'Testing Polaris...', options.pendingClass || 'text-muted');

  try {
    const headers = {};
    if (options.token) {
      headers.Authorization = options.token;
    }

    const data = await requestJson(url, {
      method: 'POST',
      headers,
      body: payload || {}
    });
    if (data && data.success) {
      setInlineResult(resultEl, options.successText || 'Success! Polaris API is working.', options.successClass || 'text-success font-weight-bold');
      return true;
    }
    setInlineResult(resultEl, 'Error: ' + (data.message || 'Failed'), options.errorClass || 'text-danger font-weight-bold');
    return false;
  } catch (err) {
    setInlineResult(resultEl, err.message || options.networkErrorText || 'Error testing Polaris.', options.errorClass || 'text-danger font-weight-bold');
    return false;
  } finally {
    if (btn) btn.disabled = false;
  }
}

export function updateAutoRejectEmailControls() {
  const enabled = getFieldChecked('outstanding-timeout-enabled');
  const sendEmail = getFieldChecked('outstanding-timeout-send-email');

  const wrappers = {
    wrapper: document.getElementById('auto-reject-email-wrapper'),
    templateWrapper: document.getElementById('auto-reject-template-wrapper'),
    help: document.getElementById('auto-reject-template-help')
  };
  const select = document.getElementById('outstanding-timeout-rejection-template-id');
  const warning = document.getElementById('auto-reject-template-warning');
  if (!wrappers.wrapper || !select) return;

  wrappers.wrapper.classList.toggle('hidden', !enabled);
  if (!enabled) {
    setFieldChecked('outstanding-timeout-send-email', false);
    setFieldValue('outstanding-timeout-rejection-template-id', '');
    return;
  }

  warning.classList.add('hidden');
  wrappers.templateWrapper.classList.toggle('hidden', !sendEmail);
  select.replaceChildren();
  select.disabled = false;

  if (!sendEmail) return;

  const standardOption = { id: '', name: 'Standard Rejection Email' };
  const allTemplates = [standardOption, ...(currentRejectionTemplates || [])];

  if (allTemplates.length === 1) {
    const opt = document.createElement('option');
    opt.value = '';
    opt.textContent = standardOption.name;
    select.appendChild(opt);
    select.value = '';
    select.disabled = true;
    wrappers.help.textContent = 'The system will use the default rejection template for this library.';
    return;
  }

  const currentSelection = select.value;
  for (const tpl of allTemplates) {
    const opt = document.createElement('option');
    opt.value = tpl.id || '';
    opt.textContent = tpl.name || tpl.subject || 'Rejection template';
    select.appendChild(opt);
  }

  if (currentSelection && allTemplates.some(t => t.id === currentSelection)) {
    select.value = currentSelection;
  } else if (workflowSettings.outstandingTimeoutRejectionTemplateId && allTemplates.some(t => t.id === workflowSettings.outstandingTimeoutRejectionTemplateId)) {
    select.value = workflowSettings.outstandingTimeoutRejectionTemplateId;
  } else {
    select.value = '';
  }

  wrappers.help.textContent = 'Select the template to use for auto-rejected suggestions. "Standard Rejection Email" is the default.';
}
