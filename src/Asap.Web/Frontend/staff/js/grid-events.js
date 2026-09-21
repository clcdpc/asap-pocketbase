import { openPolarisSearch } from './modals.js';
import { openNewSuggestionForPatron } from './patron.js';
import { showToast } from './dialogs.js';
import { renderNoteActivity } from './note-activity.js';
import { normalizeStatus } from './grid-policy.mjs';
import { renderAdditionalCopySourceCell } from './grid-rendering.js';
import { findWorkflowRow, requestIdentity, requestIdentityFromElement } from './request-identity.mjs';

function findSuggestion(identity, ctx) {
  return findWorkflowRow(identity, ctx.currentSuggestions, ctx.allSuggestions);
}

export function shouldIgnoreRowEditClick(target, event) {
  if (event.defaultPrevented) return true;
  if (event.button !== 0) return true;
  if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return true;

  return !!target.closest([
    'button',
    'a',
    'input',
    'select',
    'textarea',
    'label',
    'summary',
    '[role="button"]',
    '[role="menu"]',
    '[role="menuitem"]',
    '[data-row-action-id]',
    '[data-row-menu-action-ids]',
    '[data-no-row-edit]',
    '[data-notes-action]',
    '.row-action-group',
    '.row-action-menu',
    '.gridjs-search',
    '.gridjs-pagination'
  ].join(','));
}

export function openSuggestionEditFromRow(identity, ctx) {
  const row = findSuggestion(identity, ctx);
  if (!row) {
    showToast('Could not find that suggestion. Refresh and try again.', 'error');
    return;
  }

  const status = normalizeStatus(row.status);
  const isAdditionalCopy = row.type === 'additional_copy';
  const defaultTitle = isAdditionalCopy ? 'Edit additional-copy task' : (status === 'suggestion' ? 'Edit suggestion' : 'Edit');

  ctx.openEdit(requestIdentity(row), status || ctx.currentStatus, defaultTitle, '', 'Save');
}

export function setupGridEvents(ctx) {
  ctx.gridContainer.addEventListener('click', (e) => {
    const target = e.target;
    if (!(target instanceof Element)) return;

    const actionButton = e.target.closest('[data-row-action-id]');
    if (actionButton) {
      e.preventDefault();
      e.stopPropagation();
      const action = ctx.getRegisteredRowAction(actionButton.getAttribute('data-row-action-id'));
      if (action) ctx.runRowAction(action);
      return;
    }

    const menuTrigger = e.target.closest('[data-row-menu-action-ids]');
    if (menuTrigger) {
      e.preventDefault();
      e.stopPropagation();
      const actionIds = (menuTrigger.getAttribute('data-row-menu-action-ids') || '').split(',').filter(Boolean);
      ctx.openActionMenu(menuTrigger, actionIds);
      return;
    }

    const truncateBtn = e.target.closest('.truncate-note');
    if (truncateBtn && ctx.gridContainer.contains(truncateBtn)) {
      e.preventDefault();
      e.stopPropagation();
      const identity = requestIdentityFromElement(truncateBtn, 'data-note-record-id');
      const row = findSuggestion(identity, ctx);
      const content = document.getElementById('noteDialogContent');
      const dialog = document.getElementById('noteDialog');
      if (!row || !content || !dialog) {
        showToast('Could not find those notes. Refresh and try again.', 'error');
        return;
      }
      content.replaceChildren(renderNoteActivity(row.notes));

      if (row.type === 'additional_copy' && row.sourceTitleRequest) {
        const sourceWrapper = document.createElement('div');
        sourceWrapper.className = 'mb-3 pb-3 border-bottom';
        const strong = document.createElement('strong');
        strong.textContent = 'Original task: ';
        sourceWrapper.style.display = 'flex';
        sourceWrapper.style.alignItems = 'baseline';
        sourceWrapper.style.gap = '8px';
        sourceWrapper.append(strong, renderAdditionalCopySourceCell(row));
        content.prepend(sourceWrapper);
      }

      dialog.showModal();
      document.getElementById('noteDialogCloseBtn')?.focus();
      return;
    }

    const duplicateSummaryBtn = target.closest('.duplicate-summary-btn');
    if (duplicateSummaryBtn && ctx.gridContainer.contains(duplicateSummaryBtn)) {
      e.preventDefault();
      e.stopPropagation();
      const expanded = duplicateSummaryBtn.getAttribute('aria-expanded') === 'true';
      const detailsId = duplicateSummaryBtn.getAttribute('aria-controls');
      const details = detailsId ? document.getElementById(detailsId) : null;
      duplicateSummaryBtn.setAttribute('aria-expanded', expanded ? 'false' : 'true');
      const icon = duplicateSummaryBtn.querySelector('.duplicate-summary-icon');
      if (icon) icon.textContent = expanded ? '▸' : '▾';
      if (details) details.classList.toggle('hidden', expanded);
      return;
    }

    const tagBadge = target.closest('.flag-badge, .workflow-tag, .asap-duplicate-badge, .asap-isbn-check-badge');
    if (tagBadge && ctx.gridContainer.contains(tagBadge)) {
      e.preventDefault();
      e.stopPropagation();
      const tag = tagBadge.getAttribute('data-tag');
      if (tag) ctx.toggleTagFilter(tag);
      return;
    }

    const quickNewBtn = target.closest('.quick-new-suggestion');
    if (quickNewBtn && ctx.gridContainer.contains(quickNewBtn)) {
      e.preventDefault();
      e.stopPropagation();
      const barcode = quickNewBtn.getAttribute('data-barcode');
      if (barcode) openNewSuggestionForPatron(barcode);
      return;
    }

    const polarisSearchBtn = target.closest('.polaris-row-search');
    if (polarisSearchBtn && ctx.gridContainer.contains(polarisSearchBtn)) {
      e.preventDefault();
      e.stopPropagation();
      const identity = requestIdentityFromElement(polarisSearchBtn);
      const mode = polarisSearchBtn.getAttribute('data-polaris-search-mode') || 'title';
      const row = findSuggestion(identity, ctx);
      if (row) {
        openPolarisSearch(row, mode);
      } else {
        showToast('Could not find that suggestion. Refresh and try again.', 'error');
      }
      return;
    }

    if (shouldIgnoreRowEditClick(target, e)) return;

    const tableRow = target.closest('tr');
    if (!tableRow || !ctx.gridContainer.contains(tableRow)) return;

    const marker = tableRow.querySelector('[data-suggestion-id]');
    const identity = requestIdentityFromElement(marker);
    if (!identity.id) return;

    openSuggestionEditFromRow(identity, ctx);
  });

  document.addEventListener('click', (event) => {
    const menuActionButton = event.target.closest('#action-menu-layer [data-row-action-id]');
    if (menuActionButton) {
      event.preventDefault();
      event.stopPropagation();
      const action = ctx.getRegisteredRowAction(menuActionButton.getAttribute('data-row-action-id'));
      if (action) ctx.runRowAction(action);
      return;
    }
    const activeActionMenu = ctx.activeActionMenu;
    if (!activeActionMenu) return;
    const clickedMenu = activeActionMenu.menu.contains(event.target);
    const clickedTrigger = activeActionMenu.triggerButton.contains(event.target);
    if (!clickedMenu && !clickedTrigger) ctx.closeActionMenu();
  });

  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && ctx.activeActionMenu) {
      const trigger = ctx.activeActionMenu.triggerButton;
      ctx.closeActionMenu();
      trigger?.focus();
    }
  });
  window.addEventListener('resize', ctx.closeActionMenu);
  window.addEventListener('scroll', ctx.closeActionMenu, true);

  document.addEventListener('click', (e) => {
    if (e.target.closest('.js-close-note-dialog')) {
      const dialog = document.getElementById('noteDialog');
      if (dialog) dialog.close();
    }
  });

  document.addEventListener('asap:recent-suggestion-selected', async event => {
    const { id, type, status } = event.detail || {};
    await ctx.loadTab(status || 'suggestion');
    openSuggestionEditFromRow(requestIdentity({ id, type }), ctx);
  });
}
