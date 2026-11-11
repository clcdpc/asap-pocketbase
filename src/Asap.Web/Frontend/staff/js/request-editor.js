import { authorizedJson, isAbortError } from './http.js';
import { createLatestLoad } from '../../shared/latest-load.js';
import { applyPolarisResultToControls, renderResearchLinks, selectedStaffBibId, positivePolarisId } from './research.js';
import { customFieldValue, renderCustomFieldEditor, collectCustomFields } from './custom-fields.js';
import { element, icon, commandButton, labeledInput, selectWithHistorical } from './ui.js';

export function createRequestEditor({ context, configuration, polarisLookup, announce, footerRoot = null, closeDetail = () => {}, onDirtyChange = () => {}, onResearchConfiguration = () => {}, verified = null, retained = null, request: send = authorizedJson }) {
  const { request, actor, root, scope: interactionScope, isCurrent, isPending, admit, confirm: confirmChanges, mutate } = context;
  const reads = createLatestLoad();
  let editControls = null, editorDirty = false, editorDraft = null, verifiedBib = verified, research = null, lookupContext = null;
  let formEvents = null, linkNeedsReload = false;
  let refreshRequiredMessage = null, saveControl = null, revertControl = null, saveStateControl = null;
  let beginBibLink = () => {}, cancelBibLink = () => false, captureDraft = () => null, showBibLinkError = () => {}, needsBibLinkReload = () => false;
  function isVerifiedDraft(request) {
    return verifiedBib?.requestId === String(request.id) &&
      verifiedBib.version === request.version &&
      verifiedBib.identifier === String(editControls?.identifier.value || '').trim() &&
      positivePolarisId(editControls?.bib.value) === verifiedBib.bibId;
  }

  function hasAuthoritativeBib(request) {
    return request.bibidStaffVerified === true && positivePolarisId(request.bibid) !== null &&
      isCurrent() &&
      positivePolarisId(editControls?.bib.value) === request.bibid &&
      String(editControls?.identifier.value || '').trim() === String(request.identifier || '').trim();
  }

  function updateResearchLinks() {
    if (!isCurrent()) return;
    const container = root.querySelector('.request-research-links');
    if (!container || !request) return;
    renderResearchLinks(container, request, research, {
      title: editControls?.title.value,
      identifier: editControls?.identifier.value,
      bibId: editControls?.bib.value
    });
  }

  async function loadResearchConfiguration(request) {
    const load = reads.begin('research-configuration');
    try {
      const data = await send(
        `/api/asap/staff/research-configuration?requestId=${encodeURIComponent(request.id)}`,
        { signal: load.signal });
      if (!load.isCurrent() || !isCurrent()) return;
      research = data;
      onResearchConfiguration(data);
      updateResearchLinks();
    } catch (error) {
      if (isAbortError(error) || error.status === 401) return;
    } finally {
      reads.finish('research-configuration', load.token);
    }
  }

  function buildEditForm(request, configuration) {
    if (request.customFieldsValid === false) {
      return element('p', {
        className: 'blocked-callout wide',
        tabindex: '-1',
        role: 'alert',
        text: 'Saved custom-field history is invalid. Request edits are blocked to protect the stored values; contact an administrator.'
      });
    }

    const form = element('form', { className: 'edit-form', id: `request-edit-${request.id}` });
    formEvents?.abort();
    const events = new window.AbortController(); formEvents = events;
    const listen = (node, name, listener) => node.addEventListener(name, event => {
      if (!events.signal.aborted && isCurrent() && form.isConnected) void listener(event);
    }, { signal: events.signal });
    const draft = interactionScope.register({ root: form, kind: 'editor', isDirty: () => editorDirty });
    editorDraft = draft;
    editorDirty = false;
    const title = element('input', { value: request.title, required: 'required', maxlength: '500' });
    const author = element('input', { value: request.author || '', maxlength: '500' });
    const identifier = element('input', {
      value: request.identifier || '',
      maxlength: '100',
      disabled: !request.capabilities.canEditIdentifier
    });
    const bib = element('input', {
      id: `request-${request.id}-bib-id`,
      value: request.bibid || '',
      inputmode: 'numeric',
      pattern: '[0-9]*',
      maxlength: '100',
      disabled: !request.capabilities.canChangeBib
    });
    function identifierField(label, input, helpText = '') {
      const field = labeledInput(label, input);
      const descriptions = [], help = [];
      if (input.disabled) {
        const hint = element('small', { id: `request-${request.id}-${label === 'Identifier' ? 'identifier' : 'bib'}-locked`,
          className: 'field-lock', tabindex: '-1', 'aria-label': `${label} is locked`, text: 'Locked' });
        field.firstElementChild.id = `${hint.id}-label`;
        input.setAttribute('aria-labelledby', field.firstElementChild.id);
        descriptions.push(hint.id);
        help.push(hint);
      }
      if (helpText) {
        const hint = element('small', { id: `request-${request.id}-bib-required`,
          className: 'field-help request-bib-prerequisite-help', text: helpText });
        descriptions.push(hint.id);
        help.push(hint);
      }
      if (help.length) field.append(element('div', { className: 'form-field-help' }, help));
      if (descriptions.length) input.setAttribute('aria-describedby', descriptions.join(' '));
      return field;
    }
    editControls = { title, author, identifier, bib };
    const linkPanel = element('section', { className: 'bib-link-panel blocked-callout', hidden: true,
      'aria-labelledby': `request-${request.id}-bib-link-heading` });
    const linkHelp = element('p', { id: `request-${request.id}-bib-link-help`,
      text: 'Search Polaris and use the matching verified BIB in this form. The final action saves only the BIB association and Already own decision. Other request edits remain unsaved.' });
    const linkStatus = element('p', { id: `request-${request.id}-bib-link-status`, role: 'status', 'aria-live': 'polite' });
    let linking = false, linkReturnFocus = null;
    needsBibLinkReload = () => linkNeedsReload;
    const completeLink = commandButton(linkNeedsReload ? 'Reload current request'
      : request.autohold ? 'Save Bib ID and move to Pending hold' : 'Save Bib ID and close without hold', 'check', async () => {
      if (!linking || !isCurrent() || isPending()) return;
      if (linkNeedsReload) {
        completeLink.disabled = true;
        const refreshed = await context.reloadBibLink(captureDraft());
        if (!refreshed && isCurrent()) {
          completeLink.disabled = false;
          showBibLinkError('Current request details could not be loaded. Reload before retrying the action.', true);
          completeLink.focus();
        }
        return;
      }
      if (!admit({ consumes: draft })) {
        showBibLinkError('Finish or cancel the other request action before saving this BIB.');
        return;
      }
      const bibId = positivePolarisId(bib.value);
      if (!bibId || !isVerifiedDraft(request)) {
        showBibLinkError(bibId ? 'Search Polaris and use the matching result to verify this BIB before continuing.'
          : 'Enter a positive Polaris BIB ID up to 2147483647, then verify it with Search Polaris.');
        (bibId ? searchButton : bib).focus();
        return;
      }
      const outcome = await context.completeBibLink(bibId, draft);
      if (isCurrent() && linkPanel.isConnected && outcome?.message) {
        showBibLinkError(outcome.message, outcome.outcome === 'uncertain');
        completeLink.focus();
      }
    }, 'primary-button');
    completeLink.setAttribute('aria-describedby', linkStatus.id);
    const cancelLink = commandButton('Cancel linking', 'times', () => cancelBibLink());
    linkPanel.append(element('h3', { id: `request-${request.id}-bib-link-heading`, text: 'Link a catalog record to continue' }),
      linkHelp, element('p', { text: request.autohold ? 'Saving the verified BIB moves this request to Pending hold.'
        : 'Automatic hold placement is off. Saving the verified BIB closes this request without placing a hold.' }),
      linkStatus, element('div', { className: 'form-actions' }, [completeLink, cancelLink]));
    showBibLinkError = (message, needsReload = false) => {
      if (!isCurrent()) return;
      linkStatus.textContent = message;
      linkStatus.setAttribute('role', 'alert');
      // Local lookup/validation feedback cannot resolve an uncertain action.
      // Only a new authoritative request editor releases the recovery gate.
      linkNeedsReload ||= needsReload;
      if (linkNeedsReload) completeLink.textContent = 'Reload current request';
    };
    const selectedContext = element('p', { className: 'polaris-selection-context wide', role: 'status' });
    const searchButton = commandButton('Search Polaris catalog', 'search', () => {
      if (!form.isConnected || !isCurrent() || isPending()) return;
      lookupContext = {
        requestId: String(request.id),
        libraryOrgId: request.libraryOrgId,
        isCurrent: () => isCurrent() && form.isConnected,
        returnFocus: searchButton,
        editorFocus: bib,
        canApply: row => !bib.disabled || row.bibId === positivePolarisId(bib.value),
        mode: 'title',
        query: title.value.trim(),
        title: title.value.trim(),
        sourceAuthor: author.value.trim(),
        onError: message => { if (linking && isCurrent()) showBibLinkError(message); },
        apply: (selected, verifiedDetail) => {
          if (!isCurrent() || !form.isConnected || isPending()) return;
          // Linking owns only the BIB; retain the staff member's title draft too.
          if (linking) bib.value = String(selected.bibId);
          else applyPolarisResultToControls(selected, { bib, title, author, identifier });
          verifiedBib = {
            requestId: String(request.id),
            version: request.version,
            identifier: String(identifier.value).trim(),
            bibId: selected.bibId,
            detail: verifiedDetail
          };
          editorDirty = true;
          interactionScope.touch();
          updateResearchLinks();
          showSelectedContext();
          updatePreview();
          if (linking) {
            linkStatus.setAttribute('role', 'status');
            linkStatus.textContent = linkNeedsReload
              ? `BIB ${selected.bibId} is verified. Reload the current request before making another change.`
              : `BIB ${selected.bibId} is verified. Use the final action to save it and ${request.autohold ? 'move to Pending hold' : 'close without a hold'}.`;
          }
        }
      };
      if (isCurrent()) polarisLookup.open(lookupContext);
    });
    beginBibLink = (returnFocus = null, message = '') => {
      if (!form.isConnected || !isCurrent() || isPending() || !request.capabilities?.canChangeBib ||
          !request.capabilities?.allowedActions?.includes('alreadyOwn') || !linking && context.hasInlineAction() ||
          !linkNeedsReload && !admit({ consumes: draft })) return;
      context.showDetails?.();
      linking = true;
      linkReturnFocus = returnFocus || [...root.querySelectorAll('.action-bar button')].find(button => button.textContent.trim() === 'Already own');
      linkPanel.hidden = false;
      context.beginInlineAction(linkPanel, () => cancelBibLink());
      bib.setAttribute('aria-describedby', `${linkHelp.id} ${linkStatus.id}`);
      if (message) showBibLinkError(message);
      else if (linkNeedsReload) showBibLinkError('Reload the current request before making another change.');
      const focus = linkNeedsReload ? completeLink : searchButton;
      focus.scrollIntoView?.({ block: 'nearest' });
      focus.focus();
    };
    cancelBibLink = () => {
      if (!linking || isPending()) return false;
      if (lookupContext) polarisLookup.close(lookupContext);
      linking = false;
      linkPanel.hidden = true;
      context.endInlineAction(linkPanel);
      bib.removeAttribute('aria-describedby');
      if (linkReturnFocus?.isConnected) linkReturnFocus.focus();
      announce('Catalog linking canceled. Request edits remain unsaved.');
      return true;
    };
    function showSelectedContext() {
      const detail = verifiedBib?.detail;
      if (!detail || verifiedBib.requestId !== String(request.id)) {
        selectedContext.textContent = '';
        return;
      }
      const holdings = detail.holdingsSummary;
      selectedContext.textContent = [
        `Polaris BIB ${detail.bibId} verified for this request.`,
        detail.publication ? `Polaris publication: ${detail.publication}.` : '',
        detail.format ? `Polaris format: ${detail.format}.` : '',
        holdings ? `${holdings.myLibraryCount} item(s) at this library, ${holdings.otherLibraryCount} elsewhere; ${holdings.isHoldable ? 'holdable' : 'not holdable'}.` :
          detail.holdingsUnavailable ? 'Holdings are temporarily unavailable.' : '',
        detail.patronHasHold === true ? 'This patron already has a hold for this BIB.' :
          detail.patronHasHold === false ? 'No existing patron hold was found for this BIB.' : ''
      ].filter(Boolean).join(' ');
    }
    showSelectedContext();
    listen(bib, 'input', () => {
      verifiedBib = null;
      if (lookupContext) polarisLookup.invalidate(lookupContext);
      showSelectedContext();
      if (linking && !linkNeedsReload) {
        linkStatus.setAttribute('role', 'status');
        linkStatus.textContent = 'The BIB changed. Search Polaris to verify it before continuing.';
      }
      updateResearchLinks();
    });
    listen(identifier, 'input', () => {
      verifiedBib = null;
      if (lookupContext) polarisLookup.invalidate(lookupContext);
      showSelectedContext();
      if (linking && !linkNeedsReload) {
        linkStatus.setAttribute('role', 'status');
        linkStatus.textContent = 'The identifier changed. Search Polaris to verify the BIB before continuing.';
      }
      updateResearchLinks();
    });
    listen(title, 'input', updateResearchLinks);
    listen(form, 'input', () => { editorDirty = true; });
    listen(form, 'change', () => { editorDirty = true; });
    const publication = selectWithHistorical(configuration.publicationOptions, request.publication);
    publication.setAttribute('aria-label', 'Publication timing');
    const exactDate = element('input', { type: 'date', value: request.exactPublicationDate || '' });
    const format = selectWithHistorical(configuration.availableFormats, request.format, configuration.formatLabels || {});
    format.setAttribute('aria-label', 'Format');
    const notes = element('textarea', { maxlength: '10000' });
    notes.value = request.notes || '';
    // Native textareas normalize SQL CRLF notes to LF. Their rendered value is
    // the edit baseline; an unrelated edit must retain the original notes.
    const baselineNotes = notes.value;
    const autohold = element('input', {
      type: 'checkbox',
      checked: request.autohold,
      disabled: request.capabilities?.canChangeWorkflowState !== true
    });
    const customFields = element('div', { className: 'custom-fields wide' });
    let customFieldControls = renderCustomFieldEditor(customFields, request, configuration, format.value);
    const controls = { title, author, identifier, bib, publication, exactDate, format, notes, autohold };
    editControls.autohold = autohold;
    const value = control => control.type === 'checkbox' ? control.checked : control.value;
    const baseline = Object.fromEntries(Object.entries(controls).map(([key, control]) => [key, value(control)]));
    captureDraft = () => ({
      fields: Object.fromEntries(Object.entries(controls).filter(([key, control]) => value(control) !== baseline[key])
        .map(([key, control]) => [key, value(control)])),
      customFields: Object.fromEntries([...customFieldControls].filter(([key, control]) => control.mode !== 'hidden' &&
        control.input.value.trim() !== customFieldValue(request.customFields?.[key]).trim())
        .map(([key, control]) => [key, control.input.value])),
      verified: isVerifiedDraft(request) ? verifiedBib : null,
      linking, needsReload: linkNeedsReload
    });
    if (retained) {
      for (const [key, saved] of Object.entries(retained.fields || {})) {
        if (!controls[key]) continue;
        if (controls[key].type === 'checkbox') controls[key].checked = saved;
        else controls[key].value = saved;
      }
      customFieldControls = renderCustomFieldEditor(customFields, request, configuration, format.value, customFieldControls);
      for (const [key, saved] of Object.entries(retained.customFields || {})) {
        const field = customFieldControls.get(key);
        if (field) field.input.value = saved;
      }
      if (retained.verified?.requestId === String(request.id)) verifiedBib = { ...retained.verified, version: request.version };
    }
    const pendingPreview = element('p', { className: 'pending-audit-preview wide', role: 'status' });
    const saveState = element('strong', { className: 'request-save-state', role: 'status', 'aria-live': 'polite' });
    const save = element('button', { type: 'submit', form: form.id, className: 'primary-button' }, [icon('save'), 'Save changes']);
    saveControl = save; saveStateControl = saveState;
    const revert = commandButton('Revert changes', 'undo', async () => {
      if (!form.isConnected || !isCurrent() || isPending()) return;
      if (editorDirty && !await confirmChanges({ title: 'Revert request changes?',
        message: 'Discard unsaved request changes and revert this editor?',
        confirmLabel: 'Revert changes', cancelLabel: 'Keep editing', tone: 'danger' }, draft, true)) return;
      retained = null;
      verifiedBib = null;
      if (lookupContext) polarisLookup.invalidate(lookupContext);
      interactionScope.release(draft);
      if (linking) context.endInlineAction(linkPanel);
      form.replaceWith(buildEditForm(request, configuration));
      const updated = root.querySelector('.edit-form');
      (updated.closest('[hidden]') ? root.querySelector('[role="tab"][aria-selected="true"]') : updated.querySelector('input'))?.focus();
      announce('Unsaved request changes discarded.');
    });
    revertControl = revert;
    const close = commandButton('Close', 'times', () => {
      if (form.isConnected && isCurrent()) closeDetail();
    });
    close.classList.add('request-detail-close');
    function updatePreview() {
      const changed = [];
      const identifierChanged = !identifier.disabled && identifier.value.trim() !== (request.identifier || '').trim();
      const bibChanged = !bib.disabled && positivePolarisId(bib.value) !== positivePolarisId(request.bibid);
      const selectedBib = isVerifiedDraft(request) ? selectedStaffBibId(verifiedBib, request.id, bib.value) : null;
      let bibVerified = request.bibidStaffVerified === true;
      if (identifierChanged || bibChanged) bibVerified = false;
      if (selectedBib) bibVerified = true;
      if (title.value.trim() !== request.title) changed.push('title');
      if ((author.value.trim() || null) !== (request.author || null)) changed.push('author');
      if (identifierChanged) changed.push('identifier');
      if (bibChanged) changed.push('BIB ID');
      if (bibVerified !== (request.bibidStaffVerified === true)) changed.push('BIB verification');
      if ((publication.value.trim() || null) !== (request.publication || null)) changed.push('publication timing');
      if (exactDate.value !== (request.exactPublicationDate || '')) changed.push('exact publication date');
      if (format.value !== (request.format || '')) changed.push('format');
      if (autohold.checked !== Boolean(request.autohold)) changed.push('automatic hold');
      if (notes.value !== baselineNotes) changed.push('notes');
      if ([...customFieldControls].some(([key, control]) => control.mode !== 'hidden' &&
          control.input.value.trim() !== customFieldValue(request.customFields?.[key]).trim())) changed.push('custom fields');
      const claimantId = request.claimedByStaffUserId == null ? null : String(request.claimedByStaffUserId);
      const actorId = actor?.id == null ? null : String(actor.id);
      if (actorId && claimantId !== actorId) changed.push(claimantId ? 'claim transfer' : 'staff claim');
      editorDirty = changed.some(item => item !== 'claim transfer' && item !== 'staff claim');
      pendingPreview.textContent = changed.length
        ? `Pending changes (not saved): ${changed.join(', ')}.`
        : 'No pending changes.';
      pendingPreview.hidden = !editorDirty;
      saveState.textContent = editorDirty ? 'Unsaved changes' : 'All changes saved';
      saveState.dataset.state = editorDirty ? 'dirty' : 'clean';
      save.disabled = Boolean(refreshRequiredMessage) || !editorDirty;
      revert.disabled = Boolean(refreshRequiredMessage) || !editorDirty;
      onDirtyChange(editorDirty, autohold.checked !== Boolean(request.autohold));
    }
    listen(format, 'change', () => {
      customFieldControls = renderCustomFieldEditor(customFields, request, configuration, format.value, customFieldControls);
      updatePreview();
    });
    listen(form, 'input', updatePreview);
    listen(form, 'change', updatePreview);
    form.append(
      labeledInput('Title', title),
      labeledInput('Author', author),
      identifierField('Identifier', identifier),
      identifierField('BIB ID', bib,
        request.status === 'outstanding_purchase' && request.autohold === true && positivePolarisId(request.bibid) === null
          ? 'Required for Ready for hold' : ''),
      element('div', { className: 'wide polaris-edit-tools' }, [linkPanel, searchButton,
        element('div', { className: 'request-research-links', hidden: true }), selectedContext]),
      labeledInput('Publication timing', publication),
      labeledInput('Exact publication date', exactDate),
      labeledInput('Format', format),
      element('label', { className: 'form-field form-field-checkbox' }, [element('span', { text: 'Automatically place hold' }), autohold]),
      customFields,
      labeledInput('Notes', notes, 'wide')
    );
    const footer = element('footer', { className: 'request-save-footer wide' }, [
      element('div', { className: 'request-save-feedback' }, [saveState, pendingPreview]),
      element('div', { className: 'form-actions' }, [save, revert, close])
    ]);
    if (footerRoot) footerRoot.replaceChildren(footer); else form.append(footer);
    updatePreview();
    showSelectedContext();
    listen(form, 'submit', async event => {
      event.preventDefault();
      if (save.disabled || !form.isConnected || !admit({ consumes: draft })) return;
      if (!bib.disabled && bib.value.trim() && positivePolarisId(bib.value) === null) {
        announce('Enter a positive Polaris BIB ID up to 2147483647.', 'error');
        bib.focus();
        return;
      }
      const bibWillChange = !bib.disabled && positivePolarisId(bib.value) !== request.bibid;
      const selectedBibId = isVerifiedDraft(request) ? selectedStaffBibId(verifiedBib, request.id, bib.value) : null;
      const bibSelected = Boolean(selectedBibId);
      const turnsOffHoldForBib = request.autohold && !autohold.checked && Boolean(request.bibid);
      const closesExistingNoHold = Boolean(request.bibid) && !autohold.checked &&
        (request.status === 'outstanding_purchase' || request.status === 'pending_hold');
      const noAutoHoldConsequence = request.status === 'outstanding_purchase' || request.status === 'pending_hold'
        ? 'Save with automatic hold off? This request will close without placing a hold.'
        : 'Save with automatic hold off? Advancing this request with a verified BIB will close it without placing a hold.';
      const warnings = [];
      if ((bibWillChange || bibSelected || turnsOffHoldForBib || closesExistingNoHold) && !autohold.checked) {
        warnings.push(noAutoHoldConsequence);
      }
      if (request.claimType === 'automatic_format_rule') {
        const claimantId = request.claimedByStaffUserId == null ? null : String(request.claimedByStaffUserId);
        const transfersClaim = claimantId && claimantId !== String(actor?.id);
        const formatConsequence = format.value !== request.format
          ? `Change the format from ${request.formatLabel || request.format} to ${format.selectedOptions[0]?.textContent || format.value}?`
          : 'Save these request edits?';
        const claimConsequence = transfersClaim
          ? 'This transfers the automatic format claim from the current claimant to your manual claim.'
          : format.value !== request.format
            ? 'The new format rule may reassign or clear the automatic claim.'
            : 'Your automatic claim will remain.';
        warnings.push(`${formatConsequence} ${claimConsequence}`);
      }
      if (warnings.length && !await confirmChanges({ title: 'Save request changes?', message: warnings.join('\n\n'),
        confirmLabel: 'Save changes' }, draft)) return;
      if (!isCurrent() || !form.isConnected) return;
      saveState.textContent = 'Saving changes…';
      saveState.dataset.state = 'saving';
      const outcome = await mutate(`/api/asap/staff/title-requests/${request.id}/action`, {
        version: request.version,
        action: 'edit',
        title: title.value,
        author: author.value,
        identifier: identifier.disabled ? request.identifier : identifier.value.trim() || null,
        bibid: bib.disabled ? request.bibid : positivePolarisId(bib.value),
        ...(selectedBibId
          ? { staffSelectedBibId: selectedBibId }
          : {}),
        publication: publication.value,
        exactPublicationDate: exactDate.value || null,
        format: format.value,
        autohold: autohold.checked,
        notes: notes.value === baselineNotes ? request.notes || '' : notes.value,
        customFields: collectCustomFields(request, customFieldControls)
      }, 'Request changes saved.', draft);
      if (outcome?.refreshRequired) return;
      if (refreshRequiredMessage) return;
      if (isCurrent() && form.isConnected) {
        updatePreview();
        saveState.textContent = `Changes were not saved. ${outcome?.message || 'Review the error before trying again.'}`;
        saveState.dataset.state = 'error';
      }
    });
    // Revert replaces this mount too; repopulate from the retained configuration
    // and the new controls without waiting for another configuration request.
    renderResearchLinks(form.querySelector('.request-research-links'), request, research);
    return form;
  }

  return {
    build: () => buildEditForm(request, configuration),
    loadResearch: () => loadResearchConfiguration(request),
    isDirty: () => editorDirty, draft: () => editorDraft,
    isAutomaticHoldDraftChanged: () => Boolean(editControls?.autohold?.checked) !== Boolean(request.autohold),
    requireAuthoritativeRefresh(message) {
      refreshRequiredMessage = message;
      if (saveControl?.isConnected) saveControl.disabled = true;
      if (revertControl?.isConnected) revertControl.disabled = true;
      if (saveStateControl?.isConnected) {
        saveStateControl.textContent = message;
        saveStateControl.dataset.state = 'error';
      }
    },
    isVerifiedDraft: () => isVerifiedDraft(request), hasAuthoritativeBib: () => hasAuthoritativeBib(request),
    focusBib() {
      if (!isCurrent()) return false;
      const bib = editControls?.bib;
      if (bib?.isConnected) {
        // Center the field in the scrolling pane so the fixed save footer does
        // not cover it on short mobile viewports or at high browser zoom.
        bib.scrollIntoView?.({ block: 'center', inline: 'nearest' });
        if (!bib.disabled) {
          bib.focus({ preventScroll: true });
          return true;
        }
        const lock = bib.closest('.form-field')?.querySelector('.field-lock');
        if (lock?.isConnected) {
          lock.scrollIntoView?.({ block: 'center', inline: 'nearest' });
          lock.focus({ preventScroll: true });
          return true;
        }
        return false;
      }
      const blocker = root.querySelector('.request-details-panel .blocked-callout[tabindex="-1"]');
      if (blocker?.isConnected) {
        blocker.scrollIntoView?.({ block: 'center', inline: 'nearest' });
        blocker.focus({ preventScroll: true });
        return true;
      }
      return false;
    },
    beginBibLink: (...args) => beginBibLink(...args), cancelBibLink: () => cancelBibLink(),
    captureDraft: () => captureDraft(), showBibLinkError: (...args) => showBibLinkError(...args),
    needsBibLinkReload: () => needsBibLinkReload(),
    restoreBibLink() {
      if (retained?.linking && request.status === 'suggestion') beginBibLink();
    },
    acceptedVerification(next, body) {
      if (body?.action !== 'edit' || next.bibidStaffVerified !== true || !verifiedBib ||
          verifiedBib.requestId !== next.id || verifiedBib.bibId !== next.bibid ||
          verifiedBib.identifier !== String(next.identifier || '').trim()) return null;
      return Object.freeze({ ...verifiedBib, version: next.version });
    },
    invalidate() { reads.begin('research-configuration').abort(); if (lookupContext) polarisLookup.invalidate(lookupContext); },
    dispose() { formEvents?.abort(); reads.begin('research-configuration').abort(); if (lookupContext) polarisLookup.close(lookupContext); interactionScope.release(editorDraft); }
  };
}
