import { authorizedJson, isAbortError } from './http.js';
import { createLatestLoad } from '../../shared/latest-load.js';
import { byDisplayLabel } from './display-order.js';
import { createDraftScope } from './draft-scope.js';
import { validRequestId } from './recent-requests.js';
import { notificationOutcome, unconfirmedResponseError } from './mutation-outcome.js';
import { applyPolarisResultToControls, researchRecordValue } from './research.js';
import { renderCustomFieldEditor } from './custom-fields.js';
import { renderPatronText } from '../../shared/html.js';
import { element, icon, labeledInput, selectWithHistorical, dateTime } from './ui.js';

export function createSuggestionController({ root, trigger, sessionIdentity, polarisLookup, announce, getLibraries,
  beforeOpen, beforeClose, openCreatedTitle, openExistingTitle, onReceipt, clearReceipt,
  getScope = () => null, request: send = authorizedJson }) {
  const dom = { staffSuggestionDialog: root, staffSuggestionForm: root.querySelector('#staff-suggestion-form'),
    staffSuggestionStatus: root.querySelector('#staff-suggestion-status'), staffSuggestionBody: root.querySelector('#staff-suggestion-body'),
    staffSuggestionActions: root.querySelector('#staff-suggestion-actions') };
  const reads = createLatestLoad(), events = new window.AbortController();
  let suggestion = null, actor = null, returnFocus = null, activeAttempt = null, disposed = false, stageToken = 0;
  let clearOpeningAnnouncement = null;
  let drafts = createDraftScope(), draft = null, baseline = null, stageEvents = new window.AbortController(), lookupContext = null;
  function isCurrent(owner = suggestion) { return !disposed && root.open && owner !== null && suggestion === owner && sessionIdentity.isCurrent(actor); }
  function isCurrentGeneration(token) { return token === stageToken && isCurrent(); }
  function fingerprint() {
    return JSON.stringify([[suggestion?.verifiedBibId ?? null], ...[...dom.staffSuggestionForm.querySelectorAll('input, select, textarea')]
      .map(node => node.type === 'checkbox' ? node.checked : node.value)]);
  }
  function beginStage() {
    stageToken++; stageEvents.abort(); stageEvents = new window.AbortController();
    cancelStaffSuggestionLookup(); cancelStaffSuggestionConfiguration();
    if (lookupContext) polarisLookup.close(lookupContext); lookupContext = null;
    drafts.dispose(); drafts = createDraftScope(); draft = null; baseline = null;
    return stageToken;
  }
  function bindDraft() {
    baseline = fingerprint();
    draft = drafts.register({ root: dom.staffSuggestionForm, isDirty: () => isCurrent() && fingerprint() !== baseline });
  }
  function listen(node, name, listener) {
    const token = stageToken;
    node.addEventListener(name, event => {
      if (isCurrentGeneration(token) && !suggestion.submitting && !suggestion.outcomeUnconfirmed) void listener(event);
    }, { signal: stageEvents.signal });
  }
  function cancelStaffSuggestionLookup() { reads.begin('staff-suggestion-lookup').abort(); }
  function cancelStaffSuggestionConfiguration() { reads.begin('staff-suggestion-configuration').abort(); }
  function invalidate() { cancelStaffSuggestionLookup(); cancelStaffSuggestionConfiguration(); if (lookupContext) polarisLookup.invalidate(lookupContext); }
  function closeStaffSuggestion(options = {}) {
    if (disposed || !suggestion && !actor) return true;
    if (!options.force && (activeAttempt?.pending || suggestion?.outcomeUnconfirmed)) {
      setStaffSuggestionStatus('Creation is in progress or unconfirmed. Review its authoritative result before leaving.', 'error'); return false;
    }
    if (!options.force && !options.guarded && !beforeClose()) return false;
    const owner = actor, target = returnFocus;
    if (typeof clearOpeningAnnouncement === 'function') clearOpeningAnnouncement();
    clearOpeningAnnouncement = null;
    invalidate(); beginStage(); suggestion = null; actor = null; returnFocus = null;
    if (root.open) root.close(); dom.staffSuggestionForm.inert = false;
    if (!options.navigation && options.focusButton !== false && sessionIdentity.isCurrent(owner)) {
      const port = typeof target?.target === 'function' && target.isCurrent?.() ? target : null;
      const focus = port ? port.target() || port.fallback : target;
      if (focus?.isConnected) focus.focus(); else if (trigger?.isConnected) trigger.focus();
    }
    return true;
  }
  function open(opener = null, { barcode = null, focusReturn = null } = {}) {
    if (disposed || !sessionIdentity.actor() || !beforeOpen() || !closeStaffSuggestion({ focusButton: false })) return false;
    actor = sessionIdentity.preferences(); returnFocus = focusReturn || opener || document.activeElement;
    const queueScope = String(getScope() ?? '');
    const specificScope = Number(queueScope) > 1 && getLibraries().some(library => String(library.id) === queueScope)
      ? queueScope : null;
    const seed = typeof barcode === 'string' ? barcode.trim() : '';
    suggestion = { scopeId: actor.role === 'super_admin' ? specificScope : String(actor.organizationId),
      barcodeSeed: seed || null, stage: 'lookup' };
    renderStaffSuggestionSearch({ query: seed }); root.showModal();
    const token = stageToken;
    window.requestAnimationFrame(() => {
      if (!isCurrentGeneration(token)) return;
      const controls = suggestion.controls;
      (actor.role === 'super_admin' && !controls.scope?.value ? controls.scope : controls.queryInput)?.focus();
    });
    clearOpeningAnnouncement = announce(seed ? suggestion.scopeId ? 'Refreshing current Polaris details for this patron.'
      : 'Choose a servicing library to refresh this patron.' : 'Look up a patron to start a new suggestion.');
    if (seed && suggestion.scopeId) void lookupStaffPatron(null, seed);
    return true;
  }
  function staffSuggestionScopeOptions() {
    if (actor?.role !== 'super_admin') {
      return [{
        value: String(actor?.organizationId || ''),
        text: actor?.organizationName || 'My library'
      }];
    }
    return byDisplayLabel(getLibraries()).map(library => ({ value: library.id, text: library.name }));
  }

  function setStaffSuggestionStatus(message, kind = '') {
    dom.staffSuggestionStatus.textContent = message || '';
    dom.staffSuggestionStatus.className = `dialog-status${kind ? ` ${kind}` : ''}`;
  }

  function renderStaffSuggestionSearch({ query = '', message = '', kind = '', scopeId = null } = {}) {
    const token = beginStage();
    const current = suggestion || {};
    const options = staffSuggestionScopeOptions();
    const scope = element('select', {
      required: 'required',
      'aria-label': 'Servicing library',
      disabled: actor?.role !== 'super_admin'
    });
    if (actor?.role === 'super_admin') {
      scope.append(element('option', { value: '', text: 'Choose a servicing library' }));
    }
    for (const option of options) {
      scope.append(element('option', { value: option.value, text: option.text }));
    }
    const selectedScope = scopeId || current.scopeId ||
      (actor?.role === 'super_admin' ? '' : String(actor?.organizationId || ''));
    if (selectedScope && options.some(option => String(option.value) === String(selectedScope))) {
      scope.value = String(selectedScope);
    }
    const queryInput = element('input', {
      type: 'search',
      autocomplete: 'off',
      spellcheck: 'false',
      maxlength: '200',
      value: query,
      placeholder: 'Barcode or patron name',
      'aria-label': 'Patron barcode or name'
    });
    const help = element('small', {
      text: actor?.role === 'super_admin'
        ? 'This library will own the request. A specific queue library is preselected; All libraries requires an explicit choice.'
        : 'Search by barcode or name. Name matches are refreshed against current Polaris data before selection.'
    });
    const fields = element('div', { className: 'staff-suggestion-search-fields' }, [
      labeledInput('Servicing library', scope),
      labeledInput('Patron barcode or name', queryInput),
      help
    ]);
    dom.staffSuggestionBody.replaceChildren(fields);
    dom.staffSuggestionActions.replaceChildren(
      element('button', { type: 'button', className: 'secondary-button', onclick: () => {
        if (isCurrentGeneration(token)) closeStaffSuggestion();
      } }, 'Cancel'),
      element('button', { type: 'submit', className: 'primary-button' }, [icon('search'), 'Look up patron'])
    );
    suggestion = {
      ...current,
      stage: 'lookup',
      scopeId: scope.value || selectedScope || null,
      submitting: false,
      controls: { scope, queryInput }
    };
    listen(scope, 'change', () => {
      cancelStaffSuggestionLookup();
      cancelStaffSuggestionConfiguration();
      dom.staffSuggestionBody.querySelector('.staff-suggestion-matches')?.remove();
      suggestion = {
        ...suggestion,
        stage: 'lookup',
        scopeId: scope.value || null,
        verifiedBibId: null
      };
      setStaffSuggestionStatus(scope.value ? 'Library selected. Look up the patron to continue.' : 'Choose a servicing library.', '');
      if (scope.value && suggestion.barcodeSeed) void lookupStaffPatron(null, suggestion.barcodeSeed);
    });
    listen(queryInput, 'input', () => {
      cancelStaffSuggestionLookup();
      cancelStaffSuggestionConfiguration();
      suggestion.barcodeSeed = null;
      dom.staffSuggestionBody.querySelector('.staff-suggestion-matches')?.remove();
    });
    bindDraft();
    setStaffSuggestionStatus(message, kind);
  }

  function renderStaffSuggestionMatches(result, query, scopeId) {
    renderStaffSuggestionSearch({
      query,
      scopeId,
      message: 'More than one patron matched. Choose a candidate to refresh authoritative details.',
      kind: ''
    });
    const token = stageToken;
    const owner = suggestion;
    const list = element('div', { className: 'staff-suggestion-matches' });
    list.append(element('h3', { text: 'Choose a patron' }));
    const candidates = element('div', { className: 'staff-suggestion-candidate-list' });
    for (const match of byDisplayLabel(result.matches, item => item.name || 'Patron', item => item.barcode)) {
      const button = element('button', {
        type: 'button',
        className: 'staff-suggestion-candidate',
        onclick: () => {
          if (button.isConnected && isCurrent(owner) && isCurrentGeneration(token)) void lookupStaffPatron(null, match.barcode);
        }
      }, [
        element('strong', { text: match.name || 'Patron' }),
        element('span', { text: `${match.barcode} · Home library ${match.homeLibraryOrganizationName || match.homeLibraryOrganizationId}` })
      ]);
      candidates.append(button);
    }
    list.append(candidates);
    dom.staffSuggestionBody.append(list);
  }

  async function lookupStaffPatron(queryOverride = null, barcodeOverride = null) {
    if (!actor || !suggestion) return;
    const current = suggestion;
    const clearOpening = clearOpeningAnnouncement;
    const controls = current.controls;
    const scopeValue = controls?.scope?.value || current.scopeId;
    const scopeId = Number(scopeValue);
    const query = queryOverride === null
      ? controls?.queryInput?.value.trim() || ''
      : String(queryOverride).trim();
    const barcode = barcodeOverride === null ? null : String(barcodeOverride).trim();
    if (!Number.isInteger(scopeId) || scopeId <= 1) {
      setStaffSuggestionStatus('Choose a participating servicing library first.', 'error');
      controls?.scope?.focus();
      return;
    }
    if (!barcode && !query) {
      setStaffSuggestionStatus('Enter a patron barcode or name.', 'error');
      controls?.queryInput?.focus();
      return;
    }

    if (!drafts.admit({ consumes: draft }).allowed || !isCurrent(current)) return;
    const load = reads.begin('staff-suggestion-lookup');
    suggestion = { ...current, scopeId: String(scopeId), stage: 'lookup', submitting: false };
    const owner = suggestion, capturedActor = actor;
    setStaffSuggestionStatus(barcode ? 'Refreshing the selected patron...' : 'Looking up the patron...');
    const submit = dom.staffSuggestionActions.querySelector('button[type="submit"]');
    if (submit) submit.disabled = true;
    let configurationLoad = null;
    try {
      const result = await send('/api/asap/staff/patron-lookup', {
        method: 'POST',
        body: { query: barcode ? null : query, barcode, libraryOrgId: scopeId },
        signal: load.signal
      });
      if (!load.isCurrent() || !isCurrent(owner) || !sessionIdentity.isCurrent(capturedActor)) return;
      if (result.status === 'multiple_matches') {
        renderStaffSuggestionMatches(result, query, String(scopeId));
        return;
      }
      if (result.status === 'ineligible') {
        renderStaffSuggestionSearch({
          query,
          scopeId: String(scopeId),
          message: result.message || 'The matching patron is not eligible for this servicing library.',
          kind: 'error'
        });
        return;
      }
      if (result.status !== 'verified' || !result.patron) {
        renderStaffSuggestionSearch({
          query,
          scopeId: String(scopeId),
          message: result.message || 'No patron matched that search.',
          kind: 'error'
        });
        return;
      }
      configurationLoad = reads.begin('staff-suggestion-configuration');
      const configured = await send(
        `/api/asap/staff/suggestion-configuration?libraryOrgId=${encodeURIComponent(scopeId)}`,
        { signal: configurationLoad.signal });
      if (!load.isCurrent() || !configurationLoad.isCurrent() ||
          !isCurrent(owner) || !sessionIdentity.isCurrent(capturedActor)) return;
      if (configured.libraryOrgId !== scopeId) return;
      renderStaffSuggestionForm(result, configured.configuration);
    } catch (error) {
      if (!load.isCurrent() || !isCurrent(owner) || isAbortError(error) || error.status === 401) return;
      const message = error.response?.message || error.message || 'Patron lookup could not be completed.';
      renderStaffSuggestionSearch({ query, scopeId: String(scopeId), message, kind: 'error' });
    } finally {
      if (typeof clearOpening === 'function') clearOpening();
      if (configurationLoad) {
        reads.finish('staff-suggestion-configuration', configurationLoad.token);
      }
      reads.finish('staff-suggestion-lookup', load.token);
    }
  }

  function collectStaffCustomFields(controls) {
    const result = {};
    for (const [key, value] of controls || []) {
      if (value.mode === 'hidden') continue;
      const normalized = value.input.value.trim();
      if (normalized) result[key] = normalized;
    }
    return result;
  }

  function renderStaffSuggestionForm(result, configuration) {
    const token = beginStage();
    const context = result.patron;
    const patron = context.patron;
    const fakeRequest = { customFields: {} };
    const availableFormats = Array.isArray(configuration.availableFormats)
      ? configuration.availableFormats : [];
    const format = selectWithHistorical(availableFormats, availableFormats[0] || null, configuration.formatLabels || {});
    format.setAttribute('aria-label', 'Material format');
    format.required = true;
    const title = element('input', { maxlength: '500', autocomplete: 'off' });
    const author = element('input', { maxlength: '500', autocomplete: 'off' });
    const identifier = element('input', { maxlength: '100', autocomplete: 'off' });
    const publication = selectWithHistorical(configuration.publicationOptions, null);
    publication.options[0].textContent = 'Not specified';
    publication.value = '';
    const exactDate = element('input', { type: 'date' });
    const notes = element('textarea', { maxlength: '10000' });
    const autohold = element('input', { type: 'checkbox', checked: true });
    const emailConfirmation = element('input', { type: 'checkbox' });
    const pickup = element('select', { required: 'required', 'aria-required': 'true', 'aria-label': 'Preferred pickup location',
      'aria-describedby': 'staff-suggestion-pickup-help staff-suggestion-pickup-warning staff-suggestion-pickup-refreshed' });
    const branches = Array.isArray(context.pickupBranches) ? context.pickupBranches : [];
    for (const branch of branches) pickup.append(element('option', { value: branch.id, text: branch.label }));
    if (context.currentPreferredPickupState !== 'invalid' && context.currentPreferredPickupBranchId &&
        branches.some(branch => branch.id === context.currentPreferredPickupBranchId)) {
      pickup.value = String(context.currentPreferredPickupBranchId);
    } else {
      pickup.value = '';
      pickup.prepend(element('option', { value: '', text: 'Choose a pickup location' }));
    }
    const customFields = element('div', { className: 'custom-fields wide' });
    const formatNotice = element('div', { className: 'staff-format-notice field-help wide', hidden: true });
    const titleField = labeledInput('Title', title);
    const authorField = labeledInput('Author', author);
    const identifierField = labeledInput('Identifier / ISBN', identifier);
    const publicationField = labeledInput('Publication timing', publication);
    const exactDateField = labeledInput('Exact publication date', exactDate);
    const pickupField = labeledInput('Preferred pickup location', pickup);
    const pickupHelp = element('small', {
      id: 'staff-suggestion-pickup-help',
      className: 'field-help',
      text: "Changing this updates the patron's preferred pickup location in Polaris. Unfilled requests use the live preference when each hold is placed."
    });
    const pickupWarning = element('small', { id: 'staff-suggestion-pickup-warning', className: 'field-help',
      role: 'status', 'aria-live': 'polite', text: context.pickupWarning || '' });
    pickupWarning.hidden = !context.pickupWarning;
    const refreshed = element('small', { id: 'staff-suggestion-pickup-refreshed', className: 'field-help' }, [
      'Pickup choices refreshed: ', element('time', { datetime: context.pickupBranchesRefreshedAt,
        text: dateTime(context.pickupBranchesRefreshedAt) })
    ]);
    pickupField.append(element('div', { className: 'form-field-help' }, [pickupHelp, pickupWarning, refreshed]));

    const bib = element('input', { type: 'hidden' });
    const catalogStatus = element('p', { className: 'field-help', role: 'status', 'aria-live': 'polite' });
    const catalogButton = element('button', {
      type: 'button',
      className: 'secondary-button',
      onclick: () => {
        if (!isCurrentGeneration(token) || suggestion.submitting || suggestion.outcomeUnconfirmed) return;
        lookupContext = {
          requestId: null,
          libraryOrgId: Number(suggestion?.scopeId),
          mode: 'title',
          query: title.value,
          title: title.value,
          returnFocus: catalogButton,
          canApply: true,
          isCurrent: () => isCurrentGeneration(token) && suggestion?.stage === 'create' &&
            !suggestion.submitting && !suggestion.outcomeUnconfirmed,
          apply: selected => {
            applyPolarisResultToControls(selected, { bib, title, author, identifier });
            suggestion.verifiedBibId = selected.bibId;
            drafts.touch();
            catalogStatus.textContent = `Verified Polaris BIB ${selected.bibId} selected.`;
          },
          editorFocus: title
        };
        polarisLookup.open(lookupContext);
      }
    }, [icon('search'), 'Search Polaris catalog']);
    catalogButton.title = 'Search and select a verified Polaris record. Manual text remains unverified.';
    const catalogPanel = element('div', { className: 'polaris-edit-tools wide' }, [catalogButton, catalogStatus]);

    const controls = {
      format, title, author, identifier, publication, exactDate, notes,
      autohold, emailConfirmation, pickup, bib,
      customFieldControls: new Map(),
      titleField, authorField, identifierField, publicationField, exactDateField,
      catalogStatus
    };
    const applyFieldRule = (field, input, key, fallbackLabel, forceRequired = false) => {
      const rule = configuration.formatRules?.[format.value]?.fields?.[key] || {};
      const hidden = !forceRequired && rule.mode === 'hidden';
      field.hidden = hidden;
      input.disabled = hidden;
      input.required = forceRequired || rule.mode === 'required';
      input.setAttribute('aria-required', String(input.required));
      const label = rule.label || fallbackLabel;
      field.firstElementChild.textContent = `${label}${input.required ? ' *' : ''}`;
    };
    let submitButton;
    const updateFormat = (preserveCustomFields = false) => {
      applyFieldRule(titleField, title, 'title', 'Title', true);
      applyFieldRule(authorField, author, 'author', 'Author');
      applyFieldRule(identifierField, identifier, 'identifier', 'Identifier / ISBN');
      applyFieldRule(publicationField, publication, 'publication', 'Publication timing');
      exactDateField.hidden = publicationField.hidden;
      exactDate.disabled = publicationField.hidden;
      controls.customFieldControls = renderCustomFieldEditor(customFields, fakeRequest, configuration, format.value,
        preserveCustomFields ? controls.customFieldControls : null);
      const formatRule = configuration.formatRules?.[format.value];
      const behavior = formatRule?.messageBehavior;
      const message = behavior === 'ebookMessage'
        ? configuration.ebookMessage
        : behavior === 'eaudiobookMessage' ? configuration.eaudiobookMessage : formatRule?.message;
      formatNotice.replaceChildren();
      if (message) renderPatronText(formatNotice, message);
      formatNotice.hidden = !message;
      if (submitButton) submitButton.disabled = branches.length === 0;
    };
    listen(format, 'change', () => updateFormat(true));

    const patronCard = element('section', { className: 'staff-patron-card', 'aria-labelledby': 'staff-patron-card-title' }, [
      element('h3', { id: 'staff-patron-card-title' }, [researchRecordValue(patron.name || 'Verified patron', context.research, 'patron')]),
      element('p', { text: `Barcode: ${patron.barcode}` }),
      element('p', { text: `Registered organization: ${context.patronOrganizationName || 'Polaris organization'} (${patron.patronOrganizationId ?? 'Not recorded'})` }),
      element('p', { text: `Home library: ${patron.homeLibraryOrganizationName || 'Not recorded'} (${patron.homeLibraryOrganizationId ?? 'Not recorded'})` }),
      element('p', { text: `Servicing library: ${context.libraryOrgName || result.libraryOrgName || 'Selected library'} (${result.libraryOrgId})` }),
      element('p', { text: context.searchLibraryLimited
        ? 'Patron search is limited to this servicing library.'
        : 'Patrons from other participating home libraries may be serviced here.' }),
      element('p', { text: `Current Polaris pickup preference: ${context.currentPreferredPickupBranchName ||
        (context.currentPreferredPickupState === 'invalid' ? 'Invalid preference; choose an eligible location' :
          context.currentPreferredPickupBranchId ? `Branch ${context.currentPreferredPickupBranchId} (not currently eligible)` : 'Not recorded')}` }),
      element('p', { text: context.email || 'No patron email is recorded.' })
    ]);
    const fields = element('div', { className: 'edit-form staff-suggestion-fields' }, [
      patronCard,
      labeledInput('Material format', format, 'wide'),
      formatNotice,
      titleField,
      authorField,
      identifierField,
      publicationField,
      catalogPanel,
      exactDateField,
      pickupField,
      customFields,
      element('label', { className: 'check-field' }, [autohold, element('span', { text: 'Automatically place hold' })]),
      element('label', { className: 'check-field' }, [emailConfirmation, element('span', { text: 'Email patron confirmation (optional)' })]),
      labeledInput('Staff notes', notes, 'wide'),
      bib
    ]);
    dom.staffSuggestionBody.replaceChildren(fields);
    const changeButton = element('button', {
      type: 'button',
      className: 'secondary-button',
      onclick: () => {
        if (isCurrentGeneration(token) && beforeClose()) {
          renderStaffSuggestionSearch({ query: patron.barcode, scopeId: String(result.libraryOrgId) });
        }
      }
    }, 'Change patron');
    submitButton = element('button', { type: 'submit', className: 'primary-button' }, [icon('send'), 'Create suggestion']);
    dom.staffSuggestionActions.replaceChildren(changeButton, submitButton);
    controls.changeButton = changeButton;
    suggestion = {
      ...suggestion,
      stage: 'create',
      result,
      configuration,
      controls,
      verifiedBibId: null,
      patronIneligible: false,
      submitting: false
    };
    updateFormat();
    setStaffSuggestionStatus(
      branches.length ? 'Patron verified. Complete the configured fields, then create the suggestion.'
        : 'Pickup locations are unavailable; the suggestion cannot be created.',
      branches.length ? '' : 'error');
    if (availableFormats.length === 0) {
      setStaffSuggestionStatus('No enabled material formats are available for this library.', 'error');
      submitButton.disabled = true;
    }
    bindDraft();
    title.focus();
  }

  async function submitStaffSuggestion(event) {
    event.preventDefault();
    if (suggestion?.stage === 'create') {
      await createStaffSuggestion();
    } else {
      await lookupStaffPatron();
    }
  }

  async function createStaffSuggestion() {
    const current = suggestion;
    if (!isCurrent(current) || current.stage !== 'create' || current.submitting || current.outcomeUnconfirmed || current.patronIneligible ||
        !drafts.admit({ consumes: draft }).allowed || !dom.staffSuggestionForm.reportValidity()) return;
    const owner = actor, controls = current.controls, result = current.result;
    const body = Object.freeze({
      libraryOrgId: Number(current.scopeId),
      barcode: result.patron.patron.barcode,
      format: controls.format.value || null,
      title: controls.title.disabled ? '' : controls.title.value,
      author: controls.author.disabled ? '' : controls.author.value,
      identifier: controls.identifier.disabled ? '' : controls.identifier.value,
      publication: controls.publication.disabled ? null : controls.publication.value || null,
      exactPublicationDate: controls.exactDate.disabled ? null : controls.exactDate.value || null,
      notes: controls.notes.value,
      preferredPickupBranchId: Number(controls.pickup.value) || null,
      currentPreferredPickupBranchIdAtLoad: result.patron.currentPreferredPickupBranchId,
      currentPreferredPickupBranchObservedAtLoad: true,
      autohold: controls.autohold.checked,
      emailPatronConfirmation: controls.emailConfirmation.checked,
      customFields: collectStaffCustomFields(controls.customFieldControls),
      verifiedBibId: current.verifiedBibId || null
    });

    const attempt = { owner, body, pending: true, outcome: 'pending' }; activeAttempt = attempt;
    current.submitting = true; dom.staffSuggestionForm.inert = true;
    if (lookupContext) polarisLookup.close(lookupContext); lookupContext = null;
    const submit = dom.staffSuggestionActions.querySelector('button[type="submit"]');
    if (submit) submit.disabled = true;
    if (controls.changeButton) controls.changeButton.disabled = true;
    setStaffSuggestionStatus('Creating the suggestion...');
    try {
      const created = await send('/api/asap/staff/suggestions', { method: 'POST', body });
      if (!validRequestId(created?.id) || !Number.isSafeInteger(created?.libraryOrgId) ||
          created.libraryOrgId !== body.libraryOrgId) throw unconfirmedResponseError();
      attempt.outcome = 'committed'; attempt.pending = false;
      const notification = notificationOutcome(created.notificationStatus, created.notificationReason, 'Confirmation email');
      const notificationText = notification.text || (created.notificationStatus === 'not_requested'
        ? ' No confirmation email was requested.' : ' Confirmation email status is unconfirmed.');
      const partial = notification.partial || (!notification.text && created.notificationStatus !== 'not_requested');
      const message = `Suggestion ${created.id} created on behalf of the patron.${notificationText}`;
      onReceipt(`${message} Sign in again to review the committed request.`, owner, attempt);
      if (!isCurrent(current)) return;
      current.submitting = false; closeStaffSuggestion({ force: true, focusButton: false });
      announce(message, partial ? 'warning' : 'success');
      const review = await openCreatedTitle(Object.freeze({ id: created.id, libraryOrgId: String(created.libraryOrgId), owner, opener: trigger }));
      if (review?.queueRefreshed && review.detailLoaded) clearReceipt(attempt);
      if (review?.isCurrent()) announce(`${message}${review.queueRefreshed && review.detailLoaded ? '' : ' Current details could not be refreshed.'}`,
        review.queueRefreshed && review.detailLoaded && !partial ? 'success' : 'warning');
    } catch (error) {
      if (attempt.outcome === 'committed') return;
      const pickupChanged = error.response?.code === 'request_not_created_pickup_changed' && error.response?.pickupPreferenceChanged === true;
      const uncertain = !pickupChanged && (error.outcomeUnknown === true || !error.status || error.status === 408 || error.status >= 500 || isAbortError(error));
      attempt.outcome = pickupChanged ? 'pickup_changed' : uncertain ? 'uncertain' : 'rejected'; attempt.pending = false;
      if (pickupChanged) onReceipt(`${error.response?.message || error.message || "The suggestion was not created, but the patron's preferred pickup location was changed successfully."} Sign in again to restore staff access before continuing.`, owner, attempt);
      if (uncertain) onReceipt('Suggestion creation is unconfirmed. Sign in again and review the servicing library queue before another submission.', owner, attempt);
      if (!isCurrent(current) || error.status === 401) return;
      if (uncertain) {
        current.outcomeUnconfirmed = true;
        setStaffSuggestionStatus('Suggestion creation is unconfirmed. Reload and review the servicing library queue before attempting another submission.', 'error'); return;
      }
      if (error.response?.code === 'patron_ineligible' || error.response?.reasonCode === 'patron_ineligible') {
        current.patronIneligible = true;
        for (const field of dom.staffSuggestionBody.querySelectorAll('.staff-suggestion-fields input, .staff-suggestion-fields textarea')) {
          if (field.type === 'checkbox') field.disabled = true;
          else field.readOnly = true;
        }
        for (const control of dom.staffSuggestionBody.querySelectorAll('.staff-suggestion-fields select, .staff-suggestion-fields button')) {
          control.disabled = true;
        }
      }
      dom.staffSuggestionBody.querySelector('.staff-suggestion-conflict')?.remove();
      const duplicateId = error.response?.duplicate?.id;
      if (error.status === 409 && validRequestId(duplicateId)) {
        const match = { bibid: 'catalog BIB', identifier: 'identifier', title_format: 'title and format' }[error.response.duplicate.matchType] || 'request details';
        const panel = element('div', { className: 'staff-suggestion-conflict' }, [
          element('strong', { text: pickupChanged ? 'Pickup changed; existing suggestion found' : 'Existing suggestion found' }),
          element('span', { text: `Request ${duplicateId} already matches this patron by ${match}.` }),
          element('button', { type: 'button', className: 'secondary-button', onclick: async () => {
            if (!panel.isConnected || !isCurrent(current)) return;
            await openExistingTitle(Object.freeze({ id: duplicateId, owner, opener: trigger }));
          } }, 'Open existing request')
        ]);
        dom.staffSuggestionBody.append(panel);
      }
      setStaffSuggestionStatus(error.response?.message || error.message || 'The suggestion could not be created.', 'error');
      if (pickupChanged) clearReceipt(attempt);
    } finally {
      attempt.pending = false; if (activeAttempt === attempt) activeAttempt = null;
      if (isCurrent(current)) {
        current.submitting = false; dom.staffSuggestionForm.inert = current.outcomeUnconfirmed === true;
        if (submit?.isConnected) submit.disabled = current.outcomeUnconfirmed === true || current.patronIneligible === true;
        if (controls.changeButton?.isConnected) controls.changeButton.disabled = current.outcomeUnconfirmed === true;
        if (current.patronIneligible && controls.changeButton?.isConnected) controls.changeButton.focus();
      }
    }
  }

  dom.staffSuggestionForm.addEventListener('submit', submitStaffSuggestion, { signal: events.signal });
  for (const name of ['input', 'change']) dom.staffSuggestionForm.addEventListener(name, () => drafts.touch(), { signal: events.signal });
  trigger.addEventListener('click', event => open(event.currentTarget), { signal: events.signal });
  root.querySelector('#close-staff-suggestion').addEventListener('click', () => closeStaffSuggestion(), { signal: events.signal });
  root.addEventListener('keydown', event => { if (event.key === 'Escape') { event.preventDefault(); closeStaffSuggestion(); } }, { signal: events.signal });
  root.addEventListener('cancel', event => { event.preventDefault(); closeStaffSuggestion(); }, { signal: events.signal });
  return { open, close: closeStaffSuggestion, invalidate,
    isOpen: () => root.open,
    isDirty: () => Boolean(root.open && drafts.isDirty()),
    inspectDeparture: () => ({ owner: suggestion, dirty: Boolean(root.open && drafts.isDirty()), stamp: drafts.stamp(),
      blocked: disposed || Boolean(activeAttempt?.pending && sessionIdentity.isCurrent(activeAttempt.owner) || suggestion?.outcomeUnconfirmed),
      message: 'Creation is in progress or unconfirmed. Review its authoritative result before leaving.',
      confirmMessage: 'Discard the unsaved new suggestion and navigate away?' }),
    reportBlocked: message => setStaffSuggestionStatus(message, 'error'),
    signedOut() { if (!disposed) closeStaffSuggestion({ force: true, navigation: true, focusButton: false }); },
    dispose() {
      if (disposed) return;
      closeStaffSuggestion({ force: true, navigation: true, focusButton: false });
      disposed = true; events.abort(); stageEvents.abort(); drafts.dispose();
    }
  };
}
