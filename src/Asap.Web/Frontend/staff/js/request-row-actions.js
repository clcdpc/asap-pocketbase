import { element, icon } from './ui.js';

// Tab workflow first, then secondary commands. Capabilities are supplied by
// the server; layout only partitions this list and never grants permission.
export function eligibleRowActions(request, actor) {
  if (!['staff', 'admin', 'super_admin'].includes(actor?.role) ||
      actor.role !== 'super_admin' && String(actor.organizationId) !== String(request.libraryOrgId)) return [];
  const allowed = new Set(request.capabilities?.allowedActions || []);
  const actions = [];
  const add = (key, label, glyph, tone = 'secondary-button') => actions.push({ key, label, glyph, tone });
  if (request.status === 'suggestion') {
    if (allowed.has('purchase')) add('purchase', 'Purchase', 'shopping-cart', 'primary-button');
    if (allowed.has('reject')) add('reject', 'Reject', 'ban', 'danger-button');
    if (allowed.has('alreadyOwn')) add('alreadyOwn', 'Already own', 'book');
  } else if (request.status === 'outstanding_purchase' && allowed.has('catalogFound')) {
    add('catalogFound', request.autohold ? 'Ready for hold' : 'Close without hold', 'arrow-right', 'primary-button');
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
    const buttons = actions.map(({ key, label, glyph, tone = 'secondary-button' }) => {
      const button = element('button', { type: 'button', className: `request-row-command ${tone}`,
        'data-request-action': key, 'aria-label': `${label} request ${id}` }, [icon(glyph), label]);
      button.addEventListener('click', event => {
        if (!live(event)) return;
        const opener = popup.contains(button) ? more : button;
        close();
        onOpen(id, opener, { action: key });
      }, { signal });
      return button;
    });
    let frame = null, count = -1, availableWidth = null, controlWidths = null, anchor = null, mounted = node.isConnected, disposed = false;
    const opened = () => popup.isConnected && popup.matches(':popover-open');
    function close(restore = false) {
      if (!opened()) return;
      popup.hidePopover();
      more.setAttribute('aria-expanded', 'false');
      if (restore && more.isConnected) more.focus({ preventScroll: true });
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
      const actionIndex = buttons.indexOf(focused);
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
      const total = widths.reduce((sum, value) => sum + value, 0) + gap * (buttons.length - 1);
      let next = buttons.length;
      if (buttons.length > 1 && total > width) {
        next = 1; // Always retain a meaningful direct action, even in a tiny cell.
        let used = widths[0];
        while (next < buttons.length - 1 && used + gap + widths[next] + gap + moreWidth <= width) {
          used += gap + widths[next++];
        }
      }
      delete node.dataset.measuring;
      delete popup.dataset.measuring;
      if (next !== count) {
        close();
        // Only reparent when the partition changes; resize notifications cannot
        // repeatedly destroy focus or oscillate based on the previous layout.
        node.replaceChildren(...buttons.slice(0, next));
        if (next < buttons.length) {
          popup.replaceChildren(...buttons.slice(next)); node.append(more, popup);
        } else { popup.replaceChildren(); }
        count = next;
        if (hadFocus) {
          (actionIndex >= 0 && actionIndex < next ? buttons[actionIndex] : more.isConnected ? more : buttons[0])
            .focus({ preventScroll: true });
        }
      } else if (next === buttons.length) { more.remove(); }
      // At extreme widths the first action and More may occupy separate lines;
      // labels remain complete and the cell never expands the fixed table.
      node.classList.toggle('request-actions-wrap', next < buttons.length && widths[0] + gap + moreWidth > width);
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
