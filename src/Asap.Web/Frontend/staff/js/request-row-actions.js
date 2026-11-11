import { element, icon } from './ui.js';
import { positivePolarisId } from './research.js';

// Tab workflow first, then secondary commands. Capabilities are supplied by
// the server; layout only partitions this list and never grants permission.
export function eligibleRowActions(request, actor) {
  if (!['staff', 'admin', 'super_admin'].includes(actor?.role) ||
      actor.role !== 'super_admin' && String(actor.organizationId) !== String(request.libraryOrgId)) return [];
  const allowed = new Set(request.capabilities?.allowedActions || []);
  const actions = [];
  const add = (key, label, glyph, tone = 'secondary-button', disabled = false) => {
    const action = { key, label, glyph, tone };
    if (disabled) action.disabled = true;
    actions.push(action);
  };
  if (request.status === 'suggestion') {
    if (allowed.has('reject')) add('reject', 'Reject', 'ban', 'danger-button');
    if (allowed.has('alreadyOwn')) add('alreadyOwn', 'Already own', 'book');
  } else if (request.status === 'outstanding_purchase' && allowed.has('catalogFound')) {
    const missingSavedBibForHold = request.autohold === true && positivePolarisId(request.bibid) === null;
    add('catalogFound', request.autohold ? 'Ready for hold' : 'Close without hold', 'arrow-right', 'primary-button', missingSavedBibForHold);
  } else if (request.status === 'pending_hold' && request.capabilities?.canPlaceHold === true &&
      request.capabilities?.canChangeWorkflowState === true) {
    add('place-hold', 'Place hold', 'bookmark', 'primary-button');
  } else if (request.status === 'hold_placed' && allowed.has('close')) {
    add('close', 'Close request', 'check', 'primary-button');
  } else if (request.status === 'closed' && allowed.has('reopen')) {
    add('reopen', 'Reopen', 'undo', 'primary-button');
  }
  if (request.status !== 'closed') {
    if (!request.claimedByStaffUserId) add('claim', 'Claim', 'user-plus');
    add('assign', 'Assign', 'users');
  }
  if (request.status === 'suggestion' && allowed.has('silentClose')) add('silentClose', 'Close silently', 'archive');
  if (request.status === 'suggestion' && allowed.has('purchase')) add('purchase', 'Purchase', 'shopping-cart', 'primary-button');
  return actions;
}

// Grid.js may format an unchanged cell again while Sort/Pagination effects
// settle. Keep that cell's ref stable so those renders preserve its open menu
// and keyboard focus. A different accepted record/actor gets a fresh lifetime.
export function RequestRowActions({ request, owner, ...options }) {
  const cell = window.gridjs.useRef(null);
  const signature = JSON.stringify(options.actions);
  if (cell.current?.request !== request || cell.current?.owner !== owner || cell.current?.signature !== signature) {
    cell.current = { request, owner, signature, ref: requestRowActions(options) };
  }
  return window.gridjs.h('div', { className: 'request-row-actions', ref: cell.current.ref });
}

// This ref owns only a Grid.js cell's controls. Grid.js owns the empty host;
// moving the same buttons between host and popover never duplicates handlers.
export function requestRowActions({ id, actions, live, onOpen, fallback }) {
  let dispose = () => {};
  return node => {
    dispose();
    dispose = () => {};
    if (!node || !actions.length) return;
    const events = new window.AbortController(), signal = events.signal;
    const popup = element('div', { id: `request-row-${id}-actions`, popover: 'auto',
      className: 'request-table-menu', role: 'group', 'aria-label': `Actions for request ${id}` });
    const more = element('button', { type: 'button', className: 'request-row-more secondary-button',
      'aria-label': `More actions for request ${id}`, 'aria-controls': popup.id,
      'aria-expanded': 'false', title: 'More actions' }, icon('ellipsis-h'));
    const buttons = actions.map(({ key, label, glyph, tone = 'secondary-button', disabled = false }) => {
      const button = element('button', { type: 'button', className: `request-row-command ${tone}`,
        'data-request-action': key, 'aria-label': `${label} request ${id}`, disabled }, [icon(glyph), label]);
      button.addEventListener('click', event => {
        if (!live(event)) return;
        const opener = popup.contains(button) ? more : button;
        close();
        onOpen(id, opener, { action: key });
      }, { signal });
      return button;
    });
    let frame = null, partition = null, availableWidth = null, controlWidths = null, anchor = null, mounted = node.isConnected, disposed = false;
    const opened = () => popup.isConnected && popup.matches(':popover-open');
    function close(restore = false) {
      if (!opened()) return;
      popup.hidePopover();
      more.setAttribute('aria-expanded', 'false');
      // Hiding a popover may already restore focus without scrolling. Explicitly
      // reveal the trigger, even when the browser considers it focused already.
      if (restore && more.isConnected) {
        more.focus({ preventScroll: true });
        more.scrollIntoView?.({ block: 'nearest', inline: 'nearest' });
      }
    }
    more.addEventListener('click', event => {
      if (!live(event)) return;
      if (opened()) { close(true); return; }
      popup.showPopover();
      more.setAttribute('aria-expanded', 'true');
      const rect = more.getBoundingClientRect();
      anchor = rect;
      popup.style.left = `${Math.max(8, Math.min(rect.left, window.innerWidth - popup.offsetWidth - 8))}px`;
      popup.style.top = `${Math.max(8, Math.min(rect.bottom + 4, window.innerHeight - popup.offsetHeight - 8))}px`;
      popup.querySelector('button')?.focus({ preventScroll: true });
    }, { signal });
    popup.addEventListener('toggle', () => more.setAttribute('aria-expanded', String(opened())), { signal });
    popup.addEventListener('keydown', event => {
      if (event.key !== 'Escape') return;
      event.preventDefault(); event.stopPropagation(); close(true);
    }, { signal });
    // Focus can scroll More into view before opening and deliver its scroll
    // event afterward. Dismiss only when the positioned anchor actually moves.
    window.addEventListener('scroll', event => {
      if (!opened() || popup.contains(event.target)) return;
      const rect = more.getBoundingClientRect();
      if (rect.left !== anchor.left || rect.bottom !== anchor.bottom) close(popup.contains(document.activeElement));
    }, { capture: true, signal });
    // A fixed-width column may not resize when the viewport gets shorter or
    // narrower. Its top-layer menu still needs to leave the old coordinates.
    window.addEventListener('resize', () => {
      close(popup.contains(document.activeElement)); schedule();
    }, { signal });
    function layout() {
      frame = null;
      if (disposed) return;
      if (!node.isConnected) { if (mounted) dispose(); return; }
      mounted = true;
      if (node.clientWidth === 0) {
        close();
        if (node.contains(document.activeElement) && fallback?.isConnected) fallback.focus({ preventScroll: true });
        delete node.dataset.ready;
        return;
      }
      const focused = document.activeElement;
      const hadFocus = node.contains(focused);
      const width = node.getBoundingClientRect().width;
      if (availableWidth !== null && width !== availableWidth) close(popup.contains(document.activeElement));
      availableWidth = width;
      // Measure the actual controls in place, including commands in a closed
      // popover. This synchronous measuring style is removed before painting;
      // it creates no cloned/hidden focusable commands and never changes width.
      node.dataset.measuring = '';
      popup.dataset.measuring = '';
      if (!more.isConnected) node.append(more);
      const widths = buttons.map(button => button.getBoundingClientRect().width);
      const metrics = widths.join(',');
      if (controlWidths !== null && metrics !== controlWidths) close(popup.contains(document.activeElement));
      controlWidths = metrics;
      const gap = parseFloat(window.getComputedStyle(node).columnGap) || 0;
      const moreWidth = more.getBoundingClientRect().width;
      const purchase = buttons.find(button => button.dataset.requestAction === 'purchase');
      const otherActions = buttons.filter(button => button !== purchase);
      const primary = otherActions.find(button => ['catalogFound', 'place-hold', 'close', 'reopen'].includes(button.dataset.requestAction)) ||
        otherActions.find(button => button.dataset.requestAction === 'claim') ||
        otherActions.find(button => button.dataset.requestAction === 'assign') || otherActions[0];
      const layoutActions = primary ? [primary, ...otherActions.filter(button => button !== primary)] : otherActions;
      const widthByButton = new Map(buttons.map((button, index) => [button, widths[index]]));
      const layoutWidths = layoutActions.map(button => widthByButton.get(button));
      const actionsWidth = layoutWidths.reduce((sum, value) => sum + value, 0) + gap * Math.max(0, layoutActions.length - 1);
      const menuReservedWidth = purchase || actionsWidth > width ? gap + moreWidth : 0;
      const total = actionsWidth + menuReservedWidth;
      let directCount = layoutActions.length;
      if (layoutActions.length > 1 && total > width) {
        directCount = 1; // Always retain a meaningful direct action, even in a tiny cell.
        let used = layoutWidths[0];
        while (directCount < layoutActions.length && used + gap + layoutWidths[directCount] + menuReservedWidth <= width) {
          used += gap + layoutWidths[directCount++];
        }
      }
      const directActions = layoutActions.slice(0, directCount);
      const menuActions = [...(purchase ? [purchase] : []), ...layoutActions.slice(directCount)];
      const nextPartition = `${directActions.map(button => button.dataset.requestAction).join(',')}|${menuActions.map(button => button.dataset.requestAction).join(',')}`;
      delete node.dataset.measuring;
      delete popup.dataset.measuring;
      if (nextPartition !== partition) {
        close();
        // Only reparent when the partition changes; resize notifications cannot
        // repeatedly destroy focus or oscillate based on the previous layout.
        node.replaceChildren(...directActions);
        if (menuActions.length) {
          popup.replaceChildren(...menuActions); node.append(more, popup);
        } else { popup.replaceChildren(); }
        partition = nextPartition;
        if (hadFocus) {
          const focusTarget = directActions.includes(focused) || menuActions.includes(focused)
            ? focused : more.isConnected ? more : directActions[0];
          focusTarget?.focus({ preventScroll: true });
        }
      } else if (!menuActions.length) { more.remove(); }
      // At extreme widths the first action and More may occupy separate lines;
      // labels remain complete and the cell never expands the fixed table.
      node.classList.toggle('request-actions-wrap', menuActions.length > 0 && layoutWidths[0] + gap + moreWidth > width);
      node.dataset.ready = '';
    }
    function schedule() {
      if (!disposed && frame === null) frame = window.requestAnimationFrame(layout);
    }
    node.append(...buttons, more);
    const observer = typeof window.ResizeObserver === 'function' ? new window.ResizeObserver(schedule) : null;
    observer?.observe(node);
    // Also detect changes to rendered control metrics (font size/family, theme).
    for (const button of buttons) observer?.observe(button);
    document.fonts?.addEventListener('loadingdone', schedule, { signal });
    schedule();
    dispose = () => {
      if (disposed) return;
      disposed = true;
      if (frame !== null) window.cancelAnimationFrame(frame);
      observer?.disconnect(); events.abort();
      if (node.contains(document.activeElement) && fallback?.isConnected) fallback.focus({ preventScroll: true });
      close(); popup.remove(); node.replaceChildren();
    };
  };
}
