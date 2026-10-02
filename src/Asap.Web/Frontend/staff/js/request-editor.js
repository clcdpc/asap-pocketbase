import { authorizedJson, isAbortError } from './http.js';
import { createLatestLoad } from '../../shared/latest-load.js';
import { applyPolarisResultToControls, renderResearchLinks, selectedStaffBibId, positivePolarisId } from './research.js';
import { customFieldValue, renderCustomFieldEditor, collectCustomFields } from './custom-fields.js';
import { element, icon, commandButton, labeledInput, selectWithHistorical } from './ui.js';

export function createRequestEditor({ context, configuration, polarisLookup, announce, verified = null, request: send = authorizedJson }) {
  const { request, actor, root, scope: interactionScope, isCurrent, isPending, admit, confirm, mutate } = context;
  const reads = createLatestLoad();
  let editControls = null, editorDirty = false, editorDraft = null, verifiedBib = verified, research = null, lookupContext = null;
  let formEvents = null;
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
    const container = root.querySelector('.research-section');
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
      updateResearchLinks();
    } catch (error) {
      if (isAbortError(error) || error.status === 401) return;
    } finally {
      reads.finish('research-configuration', load.token);
    }
  }

  function buildEditForm(request, configuration) {
    const form = element('form', { className: 'edit-form' });
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
      value: request.bibid || '',
      inputmode: 'numeric',
      pattern: '[0-9]*',
      maxlength: '100',
      disabled: !request.capabilities.canChangeBib
    });
    editControls = { title, author, identifier, bib };
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
        mode: bib.value.trim() ? 'bib' : identifier.value.trim() ? 'identifier' : 'title',
        query: bib.value.trim() || identifier.value.trim() || title.value.trim(),
        title: title.value.trim(),
        author: author.value.trim(),
        apply: (selected, verifiedDetail) => {
          applyPolarisResultToControls(selected, { bib, title, author, identifier });
          verifiedBib = {
            requestId: String(request.id),
            version: request.version,
            identifier: String(identifier.value).trim(),
            bibId: selected.bibId,
            detail: verifiedDetail
          };
          editorDirty = true;
          updateResearchLinks();
          showSelectedContext();
          updatePreview();
        }
      };
      if (isCurrent()) polarisLookup.open(lookupContext);
    });
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
      updateResearchLinks();
    });
    listen(identifier, 'input', () => {
      verifiedBib = null;
      if (lookupContext) polarisLookup.invalidate(lookupContext);
      showSelectedContext();
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
    const autohold = element('input', {
      type: 'checkbox',
      checked: request.autohold,
      disabled: request.capabilities?.canChangeWorkflowState !== true
    });
    const customFields = element('div', { className: 'custom-fields wide' });
    let customFieldControls = renderCustomFieldEditor(customFields, request, configuration, format.value);
    const pendingPreview = element('p', { className: 'pending-audit-preview wide', role: 'status' });
    const save = element('button', { type: 'submit', className: 'primary-button' }, [icon('save'), 'Save changes']);
    const revert = commandButton('Revert changes', 'undo', () => {
      if (!form.isConnected || !isCurrent() || isPending()) return;
      if (editorDirty && !window.confirm('Discard unsaved request changes and revert this editor?')) return;
      verifiedBib = null;
      if (lookupContext) polarisLookup.invalidate(lookupContext);
      interactionScope.release(draft);
      form.replaceWith(buildEditForm(request, configuration));
      root.querySelector('.edit-form input')?.focus();
      announce('Unsaved request changes discarded.');
    });
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
      if (notes.value !== (request.notes || '')) changed.push('notes');
      if ([...customFieldControls].some(([key, control]) => control.mode !== 'hidden' &&
          control.input.value.trim() !== customFieldValue(request.customFields?.[key]).trim())) changed.push('custom fields');
      const claimantId = request.claimedByStaffUserId == null ? null : String(request.claimedByStaffUserId);
      const actorId = actor?.id == null ? null : String(actor.id);
      if (actorId && claimantId !== actorId) changed.push(claimantId ? 'claim transfer' : 'staff claim');
      editorDirty = changed.some(item => item !== 'claim transfer' && item !== 'staff claim');
      pendingPreview.textContent = changed.length
        ? `Pending changes (not saved): ${changed.join(', ')}.`
        : 'No pending changes.';
      save.disabled = changed.length === 0;
      revert.disabled = !editorDirty;
    }
    listen(format, 'change', () => {
      customFieldControls = renderCustomFieldEditor(customFields, request, configuration, format.value);
      updatePreview();
    });
    listen(form, 'input', updatePreview);
    listen(form, 'change', updatePreview);
    form.append(
      labeledInput('Title', title),
      labeledInput('Author', author),
      labeledInput('Identifier', identifier),
      labeledInput('BIB ID', bib),
      element('div', { className: 'wide polaris-edit-tools' }, [searchButton, selectedContext]),
      labeledInput('Publication timing', publication),
      labeledInput('Exact publication date', exactDate),
      labeledInput('Format', format),
      customFields,
      element('label', { className: 'check-field' }, [autohold, element('span', { text: 'Automatically place hold' })]),
      labeledInput('Notes', notes, 'wide'),
      pendingPreview,
      element('div', { className: 'form-actions wide' }, [save, revert])
    );
    updatePreview();
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
      if ((bibWillChange || bibSelected || turnsOffHoldForBib || closesExistingNoHold) && !autohold.checked &&
          !confirm(noAutoHoldConsequence, draft)) return;
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
        if (!confirm(`${formatConsequence} ${claimConsequence}`, draft)) return;
      }
      if (!isCurrent() || !form.isConnected) return;
      await mutate(`/api/asap/staff/title-requests/${request.id}/action`, {
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
        notes: notes.value,
        customFields: collectCustomFields(request, customFieldControls)
      }, 'Request changes saved.', draft);
    });
    return form;
  }

  return {
    build: () => buildEditForm(request, configuration),
    loadResearch: () => loadResearchConfiguration(request),
    isDirty: () => editorDirty, draft: () => editorDraft,
    isVerifiedDraft: () => isVerifiedDraft(request), hasAuthoritativeBib: () => hasAuthoritativeBib(request),
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
