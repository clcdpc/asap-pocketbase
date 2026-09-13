import {
  authorizedJson,
  isAbortError,
  latestLoads,
  loadStaffSession,
  onSessionInvalid
} from './http.js';

const STATUS_LABELS = {
  suggestion: 'Suggestion',
  outstanding_purchase: 'Outstanding purchase',
  pending_hold: 'Pending hold',
  hold_placed: 'Hold placed',
  closed: 'Closed'
};

function element(tag, attributes = {}, children = []) {
  const node = document.createElement(tag);
  for (const [name, value] of Object.entries(attributes)) {
    if (value === null || value === undefined) continue;
    if (name === 'className') node.className = value;
    else if (name === 'text') node.textContent = value;
    else if (name === 'checked') node.checked = Boolean(value);
    else if (name === 'disabled') node.disabled = Boolean(value);
    else if (name === 'value') node.value = value;
    else if (name.startsWith('on') && typeof value === 'function') node.addEventListener(name.slice(2), value);
    else node.setAttribute(name, String(value));
  }
  for (const child of Array.isArray(children) ? children : [children]) {
    if (child === null || child === undefined) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

function icon(name) {
  return element('i', { className: `fa fa-${name}`, 'aria-hidden': 'true' });
}

function commandButton(label, iconName, handler, className = 'secondary-button', disabled = false) {
  return element('button', { type: 'button', className, onclick: handler, disabled }, [icon(iconName), label]);
}

function text(value, fallback = 'Not recorded') {
  const normalized = value === null || value === undefined ? '' : String(value).trim();
  return normalized || fallback;
}

function dateTime(value) {
  if (!value) return 'Not recorded';
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? String(value) : parsed.toLocaleString();
}

function statusLabel(value) {
  return STATUS_LABELS[value] || text(value, 'Unknown');
}

function currentRequestParameter() {
  return new URL(window.location.href).searchParams.get('request');
}

function replaceRequestParameter(id) {
  const url = new URL(window.location.href);
  if (id) url.searchParams.set('request', id);
  else url.searchParams.delete('request');
  window.history.replaceState(null, '', `${url.pathname}${url.search}${url.hash}`);
}

export function createWorkflowApp() {
  const dom = {
    status: document.querySelector('#app-status'),
    signedOut: document.querySelector('#signed-out'),
    signedOutMessage: document.querySelector('#signed-out-message'),
    workspace: document.querySelector('#workspace'),
    sessionActions: document.querySelector('#session-actions'),
    staffIdentity: document.querySelector('#staff-identity'),
    signOut: document.querySelector('#sign-out'),
    queueView: document.querySelector('#queue-view'),
    profileView: document.querySelector('#profile-view'),
    viewTabs: [...document.querySelectorAll('.view-tab')],
    statusTabs: [...document.querySelectorAll('#status-tabs [data-status]')],
    scopeField: document.querySelector('#scope-field'),
    scope: document.querySelector('#library-scope'),
    search: document.querySelector('#request-search'),
    claim: document.querySelector('#claim-filter'),
    tag: document.querySelector('#tag-filter'),
    refresh: document.querySelector('#refresh-queue'),
    summary: document.querySelector('#queue-summary'),
    grid: document.querySelector('#request-grid'),
    empty: document.querySelector('#queue-empty'),
    profile: document.querySelector('#profile-form'),
    notificationEmail: document.querySelector('#notification-email'),
    weeklyEmail: document.querySelector('#weekly-email'),
    weeklyEnabled: document.querySelector('#weekly-enabled'),
    purchaseDefault: document.querySelector('#purchase-default'),
    additionalCopyDefault: document.querySelector('#additional-copy-default'),
    mineDefault: document.querySelector('#mine-default'),
    dialog: document.querySelector('#request-dialog'),
    dialogTitle: document.querySelector('#request-dialog-title'),
    dialogKicker: document.querySelector('#request-dialog-kicker'),
    dialogBody: document.querySelector('#request-dialog-body'),
    closeDialog: document.querySelector('#close-request')
  };

  const state = {
    staff: null,
    requests: [],
    scope: 'all',
    status: 'suggestion',
    grid: null,
    selectedRequestId: null,
    returnFocus: null,
    deepLinkHandled: false,
    configurations: new Map()
  };

  function announce(message, kind = '') {
    dom.status.textContent = message || '';
    dom.status.className = `status-message${kind ? ` ${kind}` : ''}`;
  }

  function showSignedOut(message) {
    state.staff = null;
    latestLoads.begin('queue').abort();
    latestLoads.begin('detail').abort();
    dom.signedOutMessage.textContent = message || 'Sign in with your authorized library account.';
    dom.signedOut.hidden = false;
    dom.workspace.hidden = true;
    dom.sessionActions.hidden = true;
    announce('');
  }

  function showWorkspace(staff) {
    state.staff = staff;
    state.scope = staff.role === 'super_admin' ? 'all' : String(staff.organizationId);
    dom.signedOut.hidden = true;
    dom.workspace.hidden = false;
    dom.sessionActions.hidden = false;
    dom.staffIdentity.textContent = staff.displayName || staff.userPrincipalName || 'Staff user';
    dom.staffIdentity.title = `${statusLabel(staff.role)} · ${staff.organizationName}`;
    dom.scopeField.hidden = staff.role !== 'super_admin';
    dom.claim.value = staff.defaultMineUnclaimedFilter ? 'mine_unclaimed' : 'all';
    populateProfile(staff);
  }

  function populateProfile(staff) {
    dom.notificationEmail.value = staff.notificationEmail || '';
    dom.weeklyEmail.value = staff.weeklyActionSummaryEmail || '';
    dom.weeklyEnabled.checked = staff.weeklyActionSummaryEnabled;
    dom.purchaseDefault.checked = staff.purchaseReminderDefault;
    dom.additionalCopyDefault.checked = staff.additionalCopyReminderDefault;
    dom.mineDefault.checked = staff.defaultMineUnclaimedFilter;
  }

  async function startSession() {
    const load = latestLoads.begin('session');
    try {
      const session = await loadStaffSession({ signal: load.signal });
      if (!load.isCurrent()) return;
      if (!session.authenticated) {
        showSignedOut();
        return;
      }
      showWorkspace(session.staff);
      announce('Staff session ready.');
      await loadQueue();
    } catch (error) {
      if (!isAbortError(error)) showSignedOut('Staff access could not be loaded. Try signing in again.');
    } finally {
      latestLoads.finish('session', load.token);
    }
  }

  function populateScopes(organizations, selectedScope) {
    dom.scope.replaceChildren(element('option', { value: 'all', text: 'All libraries' }));
    for (const organization of organizations || []) {
      dom.scope.append(element('option', { value: organization.id, text: organization.name }));
    }
    dom.scope.value = selectedScope;
  }

  function populateTags() {
    const selected = dom.tag.value;
    const tags = [...new Set(state.requests.flatMap(request => request.workflowTags || []))]
      .sort((first, second) => first.localeCompare(second));
    dom.tag.replaceChildren(element('option', { value: 'all', text: 'All tags' }));
    for (const tag of tags) dom.tag.append(element('option', { value: tag, text: tag }));
    dom.tag.value = tags.includes(selected) ? selected : 'all';
  }

  async function loadQueue(options = {}) {
    if (!state.staff) return;
    const load = latestLoads.begin('queue');
    dom.refresh.disabled = true;
    announce('Loading authorized requests...');
    try {
      const scope = state.staff.role === 'super_admin' ? state.scope : String(state.staff.organizationId);
      const result = await authorizedJson(`/api/asap/staff/title-requests?scope=${encodeURIComponent(scope)}`, {
        signal: load.signal
      });
      if (!load.isCurrent()) return;
      state.requests = Array.isArray(result.items) ? result.items : [];
      state.scope = result.scope;
      if (state.staff.role === 'super_admin') populateScopes(result.organizations, result.scope);
      populateTags();
      renderGrid();
      announce(`${state.requests.length} authorized requests loaded.`);
      if (!state.deepLinkHandled && !options.skipDeepLink) {
        state.deepLinkHandled = true;
        const deepLink = currentRequestParameter();
        if (deepLink) await openRequest(deepLink);
      }
    } catch (error) {
      if (!isAbortError(error) && error.status !== 401) {
        announce(error.message || 'Requests could not be loaded.', 'error');
      }
    } finally {
      if (load.isCurrent()) dom.refresh.disabled = false;
      latestLoads.finish('queue', load.token);
    }
  }

  function filteredRequests() {
    const query = dom.search.value.trim().toLocaleLowerCase();
    const claim = dom.claim.value;
    const tag = dom.tag.value;
    return state.requests.filter(request => {
      if (request.status !== state.status) return false;
      const mine = request.claimedByStaffUserId === state.staff.id;
      const unclaimed = !request.claimedByStaffUserId;
      if (claim === 'mine' && !mine) return false;
      if (claim === 'unclaimed' && !unclaimed) return false;
      if (claim === 'mine_unclaimed' && !mine && !unclaimed) return false;
      if (tag !== 'all' && !(request.workflowTags || []).includes(tag)) return false;
      if (!query) return true;
      return [request.title, request.author, request.nameFirst, request.nameLast,
        request.barcode, request.identifier, request.bibid, request.libraryOrgName]
        .filter(Boolean)
        .some(value => String(value).toLocaleLowerCase().includes(query));
    });
  }

  function renderGrid() {
    const requests = filteredRequests();
    dom.summary.textContent = `${requests.length} ${statusLabel(state.status).toLocaleLowerCase()} request${requests.length === 1 ? '' : 's'}`;
    dom.empty.hidden = requests.length !== 0;
    const rows = requests.map(request => [
      request.title,
      [request.nameLast, request.nameFirst].filter(Boolean).join(', ') || request.barcode,
      request.libraryOrgName,
      request.workflowTags && request.workflowTags.length ? request.workflowTags.join(', ') : 'None',
      request.claimedByDisplayName || 'Unclaimed',
      dateTime(request.updated),
      request.id
    ]);
    if (!state.grid) {
      state.grid = new window.gridjs.Grid({
        columns: [
          { name: 'Title', width: '25%' },
          { name: 'Patron', width: '17%' },
          { name: 'Library', width: '14%' },
          { name: 'Tags', width: '16%' },
          {
            name: 'Claim',
            width: '13%',
            formatter: (cell, row) => {
              const request = state.requests.find(item => item.id === row.cells[6].data);
              const mine = request && request.claimedByStaffUserId === state.staff.id;
              return window.gridjs.h('span', { className: `claim-label${mine ? ' mine' : ''}` }, cell);
            }
          },
          { name: 'Updated', width: '12%' },
          {
            name: 'Open',
            width: '74px',
            sort: false,
            formatter: id => window.gridjs.h('button', {
              type: 'button',
              className: 'grid-open',
              'aria-label': `Open request ${id}`,
              onClick: event => openRequest(String(id), event.currentTarget)
            }, [window.gridjs.h('i', { className: 'fa fa-chevron-right', 'aria-hidden': 'true' }), 'Open'])
          }
        ],
        data: rows,
        sort: true,
        pagination: { limit: 25, summary: true },
        language: { noRecordsFound: 'No requests match these filters.' }
      });
      state.grid.render(dom.grid);
    } else {
      state.grid.updateConfig({ data: rows }).forceRender();
    }
  }

  async function openRequest(id, returnFocus) {
    if (!state.staff) return;
    state.selectedRequestId = String(id);
    state.returnFocus = returnFocus || document.activeElement;
    const load = latestLoads.begin('detail');
    announce('Loading request details...');
    try {
      const request = await authorizedJson(`/api/asap/staff/title-requests/${encodeURIComponent(id)}`, {
        signal: load.signal
      });
      if (!load.isCurrent() || state.selectedRequestId !== String(id)) return;
      const configuration = await loadRequestConfiguration(request.libraryOrgId, load.signal);
      if (!load.isCurrent() || state.selectedRequestId !== String(id)) return;
      state.selectedRequestId = request.id;
      replaceRequestParameter(request.id);
      renderRequest(request, configuration);
      if (!dom.dialog.open) dom.dialog.showModal();
      dom.closeDialog.focus();
      announce(`Opened ${request.title}.`);
    } catch (error) {
      if (!isAbortError(error) && error.status !== 401) {
        announce(error.status === 404 ? 'That request is no longer available.' : error.message, 'error');
      }
    } finally {
      latestLoads.finish('detail', load.token);
    }
  }

  async function loadRequestConfiguration(organizationId, signal) {
    const key = String(organizationId);
    if (state.configurations.has(key)) return state.configurations.get(key);
    const configuration = await authorizedJson(
      `/api/asap/config?libraryOrgId=${encodeURIComponent(key)}`,
      { signal }
    );
    state.configurations.set(key, configuration);
    return configuration;
  }

  function addDetail(list, label, value) {
    const wrapper = element('div');
    wrapper.append(element('dt', { text: label }), element('dd', { text: text(value) }));
    list.append(wrapper);
  }

  function renderRequest(request, configuration) {
    dom.dialogTitle.textContent = request.title;
    dom.dialogKicker.textContent = `${request.libraryOrgName} · Request ${request.id}`;
    const body = document.createDocumentFragment();
    const meta = element('div', { className: 'detail-meta' }, [
      element('span', { className: `status-badge${request.status === 'closed' ? ' closed' : ''}`, text: statusLabel(request.status) }),
      element('span', { text: `Phase entered ${dateTime(request.phaseEnteredAt)}` }),
      element('span', { text: request.claimedByDisplayName ? `Claimed by ${request.claimedByDisplayName}` : 'Unclaimed' })
    ]);
    body.append(meta, buildActionBar(request));

    if (request.capabilities && request.capabilities.blockingReason) {
      body.append(element('p', {
        className: 'blocked-callout',
        text: request.capabilities.blockingReason === 'hold_operation_incomplete'
          ? 'Workflow-changing edits are blocked while hold placement needs recovery.'
          : 'Identifier and BIB changes are locked by this request’s placement history.'
      }));
    }

    const details = element('dl', { className: 'detail-grid' });
    addDetail(details, 'Patron', [request.nameFirst, request.nameLast].filter(Boolean).join(' '));
    addDetail(details, 'Barcode', request.barcode);
    addDetail(details, 'Email', request.email);
    addDetail(details, 'Format', request.formatLabel || request.format);
    addDetail(details, 'Identifier', request.identifier);
    addDetail(details, 'BIB ID', request.bibid);
    addDetail(details, 'Publication', request.publication);
    addDetail(details, 'Pickup', request.preferredPickupBranchName || request.preferredPickupBranchId);
    addDetail(details, 'Identifier check', request.isbnCheckStatus);
    body.append(details);

    if (request.workflowTags && request.workflowTags.length) {
      const tags = element('div', { className: 'tags', 'aria-label': 'Workflow tags' });
      for (const tag of request.workflowTags) tags.append(element('span', { className: 'tag', text: tag }));
      body.append(tags);
    }
    body.append(buildEditForm(request, configuration));
    if (request.holdOperation) body.append(buildHoldOperation(request, request.holdOperation));
    dom.dialogBody.replaceChildren(body);
  }

  function buildActionBar(request) {
    const bar = element('div', { className: 'action-bar', 'aria-label': 'Request actions' });
    const workflowBlocked = request.capabilities?.canChangeWorkflowState !== true;
    if (request.status !== 'closed') {
      if (request.claimedByStaffUserId === state.staff.id) {
        bar.append(commandButton('Unclaim', 'user-times', () => mutateSimple(request, 'unclaim')));
      } else if (!request.claimedByStaffUserId) {
        bar.append(commandButton('Claim', 'user-plus', () => mutateSimple(request, 'claim'), 'primary-button'));
      }
      if (state.staff.role === 'admin' || state.staff.role === 'super_admin') {
        bar.append(commandButton('Assign', 'users', () => showAssignment(request)));
      }
    }
    if (request.status === 'suggestion') {
      bar.append(
        commandButton('Purchase', 'shopping-cart', () => runAction(request, 'purchase'), 'primary-button', workflowBlocked),
        commandButton('Already own', 'book', () => {
          if (request.bibid) runAction(request, 'alreadyOwn');
          else announce('Add and verify a BIB ID before choosing Already own.', 'error');
        }, 'secondary-button', workflowBlocked),
        commandButton('Reject', 'ban', () => runAction(request, 'reject'), 'danger-button', workflowBlocked),
        commandButton('Close silently', 'archive', () => runAction(request, 'silentClose'), 'secondary-button', workflowBlocked)
      );
    } else if (request.status === 'outstanding_purchase') {
      bar.append(commandButton('Ready for hold', 'arrow-right', () => {
        if (request.bibid) runAction(request, 'catalogFound');
        else announce('Add and verify a BIB ID before moving this request to Pending hold.', 'error');
      }, 'primary-button', workflowBlocked));
    } else if (request.status === 'pending_hold') {
      bar.append(commandButton('Pickup', 'map-marker', () => showPickup(request), 'secondary-button', workflowBlocked));
      if (request.autohold && request.bibid) {
        bar.append(commandButton('Place hold', 'bookmark', () => mutateSimple(request, 'place-hold'), 'primary-button', workflowBlocked));
      }
      if ((request.workflowTags || []).includes('Hold exists (same patron)')) {
        bar.append(commandButton('Close duplicate', 'clone', () => runAction(request, 'closeDuplicate'), 'secondary-button', workflowBlocked));
      }
    } else if (request.status === 'hold_placed') {
      bar.append(commandButton('Close request', 'check', () => runAction(request, 'close'), 'primary-button', workflowBlocked));
    } else if (request.status === 'closed') {
      bar.append(commandButton('Reopen', 'undo', () => runAction(request, 'reopen'), 'primary-button', workflowBlocked));
    }
    if (request.capabilities && request.capabilities.canRetryIdentifierCheck) {
      bar.append(commandButton('Retry identifier check', 'refresh', () => mutateSimple(request, 'retry-identifier-check')));
    }
    return bar;
  }

  function labeledInput(label, input, className = '') {
    return element('label', { className }, [element('span', { text: label }), input]);
  }

  function selectWithHistorical(options, selectedValue, labels = {}) {
    const select = element('select');
    const values = [];
    for (const option of options || []) {
      const value = String(option);
      if (!value || values.includes(value)) continue;
      values.push(value);
      select.append(element('option', { value, text: labels[value] || value }));
    }
    const historical = selectedValue === null || selectedValue === undefined ? '' : String(selectedValue);
    if (historical && !values.includes(historical)) {
      select.append(element('option', { value: historical, text: labels[historical] || historical }));
    }
    select.value = historical || values[0] || '';
    return select;
  }

  function customFieldValue(value) {
    if (value === null || value === undefined) return '';
    if (typeof value === 'object' && !Array.isArray(value)) return String(value.value ?? '');
    return String(value);
  }

  function renderCustomFieldEditor(container, request, configuration, formatCode) {
    const controls = new Map();
    const definitions = Array.isArray(configuration.additionalFieldDefinitions)
      ? configuration.additionalFieldDefinitions
      : [];
    const existing = request.customFields && typeof request.customFields === 'object'
      ? request.customFields
      : {};
    const customRules = configuration.formatRules?.[formatCode]?.customFields || {};
    const fields = [];

    for (const definition of definitions) {
      const key = String(definition.key || definition.id || '');
      if (!key) continue;
      const rule = customRules[key] || { mode: 'hidden' };
      const hasHistorical = Object.prototype.hasOwnProperty.call(existing, key);
      if (rule.mode === 'hidden' && !hasHistorical) continue;
      const currentValue = customFieldValue(existing[key]);
      let input;
      if (definition.type === 'textarea') {
        input = element('textarea', { maxlength: '2000' });
        input.value = currentValue;
      } else if (definition.type === 'select') {
        input = element('select');
        input.append(element('option', { value: '', text: '' }));
        const knownValues = [];
        for (const option of definition.options || []) {
          if (option.enabled === false) continue;
          const value = String(option.id || option.key || '');
          if (!value || knownValues.includes(value)) continue;
          knownValues.push(value);
          input.append(element('option', { value, text: option.label || value }));
        }
        if (currentValue && !knownValues.includes(currentValue)) {
          const historicalLabel = existing[key] && typeof existing[key] === 'object'
            ? existing[key].displayValue
            : null;
          input.append(element('option', { value: currentValue, text: historicalLabel || currentValue }));
        }
        input.value = currentValue;
      } else {
        input = element('input', { type: 'text', maxlength: '250', value: currentValue });
      }
      const required = rule.mode === 'required';
      const label = `${definition.label || key}${required ? ' *' : ''}`;
      input.required = required;
      input.setAttribute('aria-required', String(required));
      input.setAttribute('aria-label', label);
      controls.set(key, { definition, input, mode: rule.mode });
      const field = labeledInput(label, input);
      if (definition.helpText) field.append(element('small', { text: definition.helpText }));
      fields.push(field);
    }
    container.replaceChildren(...fields);
    container.hidden = fields.length === 0;
    return controls;
  }

  function collectCustomFields(request, controls) {
    const existing = request.customFields && typeof request.customFields === 'object'
      ? request.customFields
      : {};
    const result = { ...existing };
    for (const [key, value] of controls) {
      if (value.mode === 'hidden') continue;
      const normalized = value.input.value.trim();
      if (!normalized) {
        delete result[key];
        continue;
      }
      result[key] = {
        label: value.definition.label || key,
        type: value.definition.type || 'text',
        value: normalized
      };
      if (value.definition.type === 'select') {
        result[key].displayValue = value.input.selectedOptions[0]?.textContent || normalized;
      }
    }
    return result;
  }

  function buildEditForm(request, configuration) {
    const form = element('form', { className: 'edit-form' });
    const title = element('input', { value: request.title, required: 'required', maxlength: '500' });
    const author = element('input', { value: request.author || '', maxlength: '500' });
    const identifier = element('input', {
      value: request.identifier || '',
      maxlength: '100',
      disabled: !request.capabilities.canEditIdentifier
    });
    const bib = element('input', {
      value: request.bibid || '',
      inputmode: 'numeric',
      pattern: '[0-9]*',
      maxlength: '100',
      disabled: !request.capabilities.canChangeBib
    });
    const publication = selectWithHistorical(configuration.publicationOptions, request.publication);
    publication.setAttribute('aria-label', 'Publication timing');
    const exactDate = element('input', { type: 'date', value: request.exactPublicationDate || '' });
    const format = selectWithHistorical(configuration.availableFormats, request.format, configuration.formatLabels || {});
    format.setAttribute('aria-label', 'Format');
    const notes = element('textarea', { maxlength: '10000' });
    notes.value = request.notes || '';
    const autohold = element('input', {
      type: 'checkbox',
      checked: request.autohold,
      disabled: request.capabilities?.canChangeWorkflowState !== true
    });
    const customFields = element('div', { className: 'custom-fields wide' });
    let customFieldControls = renderCustomFieldEditor(customFields, request, configuration, format.value);
    format.addEventListener('change', () => {
      customFieldControls = renderCustomFieldEditor(customFields, request, configuration, format.value);
    });
    form.append(
      labeledInput('Title', title),
      labeledInput('Author', author),
      labeledInput('Identifier', identifier),
      labeledInput('BIB ID', bib),
      labeledInput('Publication timing', publication),
      labeledInput('Exact publication date', exactDate),
      labeledInput('Format', format),
      customFields,
      element('label', { className: 'check-field' }, [autohold, element('span', { text: 'Automatically place hold' })]),
      labeledInput('Notes', notes, 'wide'),
      element('div', { className: 'form-actions wide' }, [
        element('button', { type: 'submit', className: 'primary-button' }, [icon('save'), 'Save changes'])
      ])
    );
    form.addEventListener('submit', async event => {
      event.preventDefault();
      await mutateRequest(request, `/api/asap/staff/title-requests/${request.id}/action`, {
        version: request.version,
        action: 'edit',
        title: title.value,
        author: author.value,
        identifier: identifier.disabled ? request.identifier : identifier.value.trim() || null,
        bibid: bib.disabled ? request.bibid : bib.value.trim() || null,
        publication: publication.value,
        exactPublicationDate: exactDate.value || null,
        format: format.value,
        autohold: autohold.checked,
        notes: notes.value,
        customFields: collectCustomFields(request, customFieldControls)
      }, 'Request changes saved.');
    });
    return form;
  }

  async function mutateSimple(request, operation) {
    const path = operation === 'claim' || operation === 'unclaim' || operation === 'retry-identifier-check' || operation === 'place-hold'
      ? `/api/asap/staff/title-requests/${request.id}/${operation}`
      : null;
    if (!path) return;
    await mutateRequest(request, path, { version: request.version },
      operation === 'place-hold' ? 'Hold placement completed.' : 'Request updated.');
  }

  async function runAction(request, action, targetStatus) {
    const terminal = action === 'reject' || action === 'silentClose' || action === 'closeDuplicate' || targetStatus === 'closed';
    if (terminal && !window.confirm('Apply this final workflow action?')) return;
    await mutateRequest(request, `/api/asap/staff/title-requests/${request.id}/action`, {
      version: request.version,
      action,
      status: targetStatus,
      emailPurchaseReminder: action === 'purchase' && state.staff.purchaseReminderDefault
    }, 'Workflow action completed.');
  }

  async function mutateRequest(request, path, body, successMessage) {
    announce('Saving request...');
    try {
      await authorizedJson(path, { method: 'POST', body });
      await loadQueue({ skipDeepLink: true });
      await openRequest(request.id);
      announce(successMessage, 'success');
    } catch (error) {
      if (error.status === 409) {
        const message = error.message || 'The request changed. Review the refreshed version before trying again.';
        await loadQueue({ skipDeepLink: true });
        await openRequest(request.id);
        announce(message, 'error');
      } else if (error.status !== 401 && !isAbortError(error)) {
        announce(error.message || 'The request could not be updated.', 'error');
      }
    }
  }

  async function showAssignment(request) {
    announce('Loading eligible staff...');
    try {
      const result = await authorizedJson(`/api/asap/staff/users?orgId=${request.libraryOrgId}`);
      const select = element('select', { 'aria-label': 'Assign to staff member' });
      const users = (result.users || []).filter(user => user.active &&
        (user.role === 'super_admin' || user.organizationId === request.libraryOrgId));
      for (const user of users) {
        select.append(element('option', {
          value: user.id,
          text: user.displayName || user.userPrincipalName || `Staff ${user.id}`
        }));
      }
      const form = element('form', { className: 'inline-form' }, [
        labeledInput('Assign request', select),
        element('button', { type: 'submit', className: 'primary-button', disabled: users.length === 0 }, [icon('user-plus'), 'Assign'])
      ]);
      form.addEventListener('submit', async event => {
        event.preventDefault();
        await mutateRequest(request, `/api/asap/staff/title-requests/${request.id}/assign`, {
          version: request.version,
          assigneeId: Number(select.value)
        }, 'Request assigned.');
      });
      dom.dialogBody.prepend(form);
      select.focus();
      announce(users.length ? 'Choose an assignee.' : 'No eligible staff are available.');
    } catch (error) {
      if (error.status !== 401) announce(error.message || 'Assignable staff could not be loaded.', 'error');
    }
  }

  async function showPickup(request) {
    announce('Loading current pickup preference...');
    try {
      const options = await authorizedJson(`/api/asap/staff/title-requests/${request.id}/pickup-options`, {
        method: 'POST',
        body: { forceRefresh: false }
      });
      const select = element('select', { 'aria-label': 'Preferred pickup branch' });
      for (const branch of options.pickupBranches || []) {
        select.append(element('option', { value: branch.id, text: branch.name }));
      }
      if (options.selectedPickupBranchId) select.value = String(options.selectedPickupBranchId);
      const form = element('form', { className: 'inline-form' }, [
        labeledInput('Preferred pickup branch', select),
        element('button', { type: 'submit', className: 'primary-button', disabled: options.readOnly }, [icon('map-marker'), 'Update pickup'])
      ]);
      if (options.pickupBranchWarning) form.append(element('p', { className: 'wide', text: options.pickupBranchWarning }));
      form.addEventListener('submit', async event => {
        event.preventDefault();
        await mutateRequest(request, `/api/asap/staff/title-requests/${request.id}/pickup-preference`, {
          version: options.version,
          preferredPickupBranchId: Number(select.value),
          currentPreferredPickupBranchIdAtLoad: options.currentPreferredPickupBranchId
        }, 'Pickup preference updated.');
      });
      dom.dialogBody.prepend(form);
      select.focus();
      announce('Current pickup preference loaded.');
    } catch (error) {
      if (error.status !== 401) announce(error.message || 'Pickup choices could not be loaded.', 'error');
    }
  }

  function buildHoldOperation(request, operation) {
    const section = element('section', { className: 'hold-operation' });
    section.append(
      element('h3', { text: operation.state === 'succeeded' ? 'Hold tracking' : 'Hold placement recovery' }),
      element('p', { text: `State: ${operation.state}; phase: ${operation.phase}; attempt: ${operation.attemptNumber}.` })
    );
    if (operation.lastErrorCode) section.append(element('p', { text: `Last diagnostic: ${operation.lastErrorCode}` }));
    if (operation.canReconcile) {
      section.append(commandButton('Reconcile provider state', 'search', async () => {
        await mutateOperation(request, operation, 'reconcile', { version: operation.version });
      }));
    }
    if (operation.canResolveSucceeded || operation.canResolveNotPerformed) {
      section.append(buildResolutionForm(request, operation));
    }
    return section;
  }

  function buildResolutionForm(request, operation) {
    const markedMutation = operation.phase === 'create_started' || operation.phase === 'reply_started';
    const outcome = element('select', { 'aria-label': 'Resolution' });
    if (operation.canResolveSucceeded) outcome.append(element('option', { value: 'succeeded', text: 'Confirmed succeeded' }));
    if (operation.canResolveNotPerformed) outcome.append(element('option', { value: 'not_performed', text: 'Confirmed not performed' }));
    const evidence = element('select', { 'aria-label': 'Evidence type' });
    const reference = element('input', { maxlength: '1000' });
    const proofSource = element('input', { maxlength: '500' });
    const causalConnection = element('textarea', { maxlength: '2000' });
    const finalHoldId = element('input', { maxlength: '100', inputmode: 'numeric', pattern: '[1-9][0-9]*' });
    const reason = element('textarea', { required: 'required', maxlength: '2000' });
    const excluded = element('input', { type: 'checkbox' });
    const proofAttested = element('input', { type: 'checkbox' });
    const exclusionAttested = element('input', { type: 'checkbox' });
    const exclusionReference = element('input', { maxlength: '1000' });
    const exclusionExplanation = element('textarea', { maxlength: '2000' });
    const form = element('form', { className: 'resolution-form' });
    const referenceField = labeledInput('Evidence reference', reference, 'wide');
    const proofSourceField = labeledInput('Evidence provenance', proofSource, 'wide');
    const causalConnectionField = labeledInput('Connection to this exact attempt', causalConnection, 'wide');
    const finalHoldIdField = labeledInput('Proven final hold ID', finalHoldId, 'wide');
    const proofAttestationField = element('label', { className: 'check-field wide' }, [
      proofAttested,
      element('span', { text: 'I attest that this evidence proves the definitive outcome for this exact operation, attempt, frozen patron, and BIB.' })
    ]);
    const excludedField = element('label', { className: 'check-field wide' }, [
      excluded,
      element('span', { text: 'I confirm every responsible or superseded worker, interactive host, and overlapping process has actually ended or been terminated.' })
    ]);
    const exclusionReferenceField = labeledInput('Executor exclusion reference', exclusionReference, 'wide');
    const exclusionExplanationField = labeledInput('Executor exclusion and in-flight work account', exclusionExplanation, 'wide');
    const exclusionAttestationField = element('label', { className: 'check-field wide' }, [
      exclusionAttested,
      element('span', { text: 'I attest that the exclusion record identifies the affected executions, when and how they ended, and accounts for provider work already sent.' })
    ]);

    function configureField(wrapper, control, visible, required = false) {
      wrapper.hidden = !visible;
      control.disabled = !visible;
      control.required = visible && required;
    }

    function updateEvidence() {
      evidence.replaceChildren();
      if (outcome.value === 'succeeded') {
        evidence.append(
          element('option', { value: 'authoritative_correlated_hold', text: 'Correlated final hold ID' }),
          element('option', { value: 'provider_final_success', text: 'Provider final success' })
        );
      } else {
        if (operation.phase === 'acquired') {
          evidence.append(element('option', { value: 'fenced_never_dispatched', text: 'Server-fenced, never dispatched' }));
        } else {
          evidence.append(element('option', { value: 'provider_final_no_effect', text: 'Provider final no-effect result' }));
        }
      }
      updateEvidenceFields();
    }
    function updateEvidenceFields() {
      const serverFenced = evidence.value === 'fenced_never_dispatched';
      const correlated = evidence.value === 'authoritative_correlated_hold';
      configureField(referenceField, reference, !serverFenced, true);
      configureField(proofSourceField, proofSource, !serverFenced, true);
      configureField(causalConnectionField, causalConnection, !serverFenced, true);
      configureField(proofAttestationField, proofAttested, !serverFenced, true);
      configureField(finalHoldIdField, finalHoldId, correlated, correlated);
      configureField(excludedField, excluded, markedMutation && !serverFenced, true);
      configureField(exclusionReferenceField, exclusionReference, markedMutation && !serverFenced, true);
      configureField(exclusionExplanationField, exclusionExplanation, markedMutation && !serverFenced, true);
      configureField(exclusionAttestationField, exclusionAttested, markedMutation && !serverFenced, true);
    }
    outcome.addEventListener('change', updateEvidence);
    evidence.addEventListener('change', updateEvidenceFields);
    form.append(
      element('p', {
        className: 'resolution-context wide',
        text: `Operation ${operation.id}; attempt ${operation.attemptNumber}; epoch ${operation.executionEpoch}; frozen patron ${operation.patronBarcodeSnapshotMasked}; frozen BIB ${operation.bibIdSnapshot}.`
      }),
      labeledInput('Resolution', outcome),
      labeledInput('Evidence type', evidence),
      referenceField,
      proofSourceField,
      causalConnectionField,
      finalHoldIdField,
      proofAttestationField,
      excludedField,
      exclusionReferenceField,
      exclusionExplanationField,
      exclusionAttestationField,
      labeledInput('Reason', reason, 'wide'),
      element('div', { className: 'form-actions wide' }, [
        element('button', { type: 'submit', className: 'danger-button' }, [icon('check-circle'), 'Resolve operation'])
      ])
    );
    updateEvidence();
    form.addEventListener('submit', async event => {
      event.preventDefault();
      if (!window.confirm('Resolve this operation using the recorded authoritative evidence?')) return;
      await mutateOperation(request, operation, 'resolve', {
        version: operation.version,
        requestVersion: request.version,
        outcome: outcome.value,
        reason: reason.value,
        evidenceKind: evidence.value,
        evidenceReference: reference.disabled ? null : reference.value,
        operationSpecificProofAttested: !proofAttested.disabled && proofAttested.checked,
        proofSource: proofSource.disabled ? null : proofSource.value,
        causalConnection: causalConnection.disabled ? null : causalConnection.value,
        provenFinalHoldId: finalHoldId.disabled ? null : finalHoldId.value,
        originalExecutorExcluded: !excluded.disabled && excluded.checked,
        executorExclusionAttested: !exclusionAttested.disabled && exclusionAttested.checked,
        executorExclusionReference: exclusionReference.disabled ? null : exclusionReference.value,
        executorExclusionExplanation: exclusionExplanation.disabled ? null : exclusionExplanation.value
      });
    });
    return form;
  }

  async function mutateOperation(request, operation, action, body) {
    announce(`${action === 'resolve' ? 'Resolving' : 'Reconciling'} hold operation...`);
    try {
      await authorizedJson(`/api/asap/staff/hold-operations/${operation.id}/${action}`, { method: 'POST', body });
      await loadQueue({ skipDeepLink: true });
      await openRequest(request.id);
      announce('Hold recovery state updated.', 'success');
    } catch (error) {
      if (error.status === 409) {
        await loadQueue({ skipDeepLink: true });
        await openRequest(request.id);
      }
      if (error.status !== 401) announce(error.message || 'Hold recovery could not be updated.', 'error');
    }
  }

  async function saveProfile(event) {
    event.preventDefault();
    announce('Saving profile...');
    try {
      const result = await authorizedJson('/api/asap/staff/profile', {
        method: 'POST',
        body: {
          version: state.staff.version,
          weeklyActionSummaryEnabled: dom.weeklyEnabled.checked,
          weeklyActionSummaryEmail: dom.weeklyEmail.value.trim() || null,
          purchaseReminderDefault: dom.purchaseDefault.checked,
          additionalCopyReminderDefault: dom.additionalCopyDefault.checked,
          defaultMineUnclaimedFilter: dom.mineDefault.checked
        }
      });
      state.staff = result.staff;
      populateProfile(state.staff);
      dom.claim.value = state.staff.defaultMineUnclaimedFilter ? 'mine_unclaimed' : 'all';
      renderGrid();
      announce('Profile saved.', 'success');
    } catch (error) {
      if (error.status === 409) {
        const session = await loadStaffSession();
        if (session.authenticated) {
          state.staff = session.staff;
          populateProfile(session.staff);
        }
      }
      if (error.status !== 401) announce(error.message || 'Profile could not be saved.', 'error');
    }
  }

  function switchView(name) {
    dom.queueView.hidden = name !== 'queue';
    dom.profileView.hidden = name !== 'profile';
    for (const tab of dom.viewTabs) {
      const active = tab.dataset.view === name;
      tab.classList.toggle('active', active);
      if (active) tab.setAttribute('aria-current', 'page');
      else tab.removeAttribute('aria-current');
    }
    document.querySelector(name === 'queue' ? '#queue-title' : '#profile-title').focus({ preventScroll: true });
  }

  function closeDialog() {
    if (dom.dialog.open) dom.dialog.close();
    state.selectedRequestId = null;
    replaceRequestParameter(null);
    if (state.returnFocus && state.returnFocus.isConnected) state.returnFocus.focus();
  }

  function bindEvents() {
    onSessionInvalid(() => showSignedOut('Your staff session ended or no longer has access. Sign in again.'));
    dom.signOut.addEventListener('click', async () => {
      try { await authorizedJson('/api/asap/staff/sign-out', { method: 'POST' }); } catch { /* Local UI still returns to sign-in. */ }
      showSignedOut('You are signed out.');
    });
    dom.refresh.addEventListener('click', () => loadQueue({ skipDeepLink: true }));
    dom.scope.addEventListener('change', () => {
      state.scope = dom.scope.value;
      loadQueue({ skipDeepLink: true });
    });
    for (const tab of dom.statusTabs) {
      tab.addEventListener('click', () => {
        state.status = tab.dataset.status;
        for (const item of dom.statusTabs) item.setAttribute('aria-selected', String(item === tab));
        renderGrid();
      });
      tab.addEventListener('keydown', event => {
        if (!['ArrowLeft', 'ArrowRight'].includes(event.key)) return;
        event.preventDefault();
        const index = dom.statusTabs.indexOf(tab);
        const offset = event.key === 'ArrowRight' ? 1 : -1;
        dom.statusTabs[(index + offset + dom.statusTabs.length) % dom.statusTabs.length].focus();
      });
    }
    dom.search.addEventListener('input', renderGrid);
    dom.claim.addEventListener('change', renderGrid);
    dom.tag.addEventListener('change', renderGrid);
    dom.profile.addEventListener('submit', saveProfile);
    for (const tab of dom.viewTabs) tab.addEventListener('click', () => switchView(tab.dataset.view));
    dom.closeDialog.addEventListener('click', closeDialog);
    dom.dialog.addEventListener('cancel', event => {
      event.preventDefault();
      closeDialog();
    });
  }

  return {
    async start() {
      bindEvents();
      await startSession();
    }
  };
}
