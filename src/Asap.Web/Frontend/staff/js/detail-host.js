// The host owns the frame and a single lease. Mounted owners own the content
// and supply their close, disposal and live-focus policies through the lease.
export function createDetailHost({ root }) {
  const body = root.querySelector('#request-dialog-body');
  const title = root.querySelector('#request-dialog-title');
  const kicker = root.querySelector('#request-dialog-kicker');
  const status = root.querySelector('#request-dialog-status');
  const close = root.querySelector('#close-request');
  const events = new window.AbortController();
  let mounted = null;
  let cancelFocus = null;
  let disposed = false;

  function cancelFocusReturn() { cancelFocus?.(); cancelFocus = null; }

  function clearHeading() {
    title.textContent = 'Request'; kicker.textContent = '';
    status.textContent = ''; status.hidden = true; status.classList.remove('closed');
    root.removeAttribute('aria-describedby');
  }

  function returnFocus(port) {
    if (!port) return;
    const grid = port.root();
    if (!grid?.isConnected) return;
    let frame, focused;
    const move = () => {
      if (disposed || mounted || root.open || !port.isCurrent()) { cancelFocusReturn(); return; }
      const target = port.target() || (port.fallback?.isConnected && !root.contains(port.fallback) ? port.fallback : null);
      if (target?.isConnected) { focused = target; target.focus(); }
    };
    const changed = event => {
      if (event.target !== focused && !root.contains(event.target)) cancelFocusReturn();
    };
    // Grid.js can render old rows before replacing them; follow the live opener
    // until a newer owner, context or deliberate focus movement takes over.
    const observer = new window.MutationObserver(move);
    cancelFocus = () => {
      observer.disconnect(); window.cancelAnimationFrame(frame);
      document.removeEventListener('focusin', changed);
    };
    observer.observe(grid, { childList: true, subtree: true });
    document.addEventListener('focusin', changed);
    frame = window.requestAnimationFrame(move);
  }

  function release(owner, options = {}) {
    if (mounted !== owner) return false;
    mounted = null;
    cancelFocusReturn();
    owner.dispose?.(options);
    if (root.open) root.close();
    body.replaceChildren();
    clearHeading();
    returnFocus(options.focusReturn);
    return true;
  }

  close.addEventListener('click', () => mounted?.onClose?.(), { signal: events.signal });
  root.addEventListener('cancel', event => {
    event.preventDefault();
    (mounted?.onEscape || mounted?.onClose)?.();
  }, { signal: events.signal });
  return {
    isOpen: () => root.open,
    requestClose: options => mounted?.onClose?.(options) ?? true,
    contains: node => root.contains(node),
    focusClose: () => { if (!disposed && mounted && root.open && close.isConnected) close.focus(); },
    cancelFocusReturn,
    acquire(owner) {
      if (disposed) throw new Error('The detail host is disposed.');
      cancelFocusReturn();
      if (mounted) release(mounted);
      clearHeading();
      mounted = owner;
      const content = document.createElement('div');
      content.className = 'detail-content';
      body.replaceChildren(content);
      const isCurrent = () => !disposed && root.isConnected && mounted === owner;
      return Object.freeze({ content, isCurrent,
        heading(value, subtitle, state = null) {
          if (!isCurrent()) return;
          title.textContent = value; kicker.textContent = subtitle;
          status.textContent = state?.label || ''; status.hidden = !state?.label;
          status.classList.toggle('closed', state?.closed === true);
          if (state?.label) root.setAttribute('aria-describedby', status.id);
          else root.removeAttribute('aria-describedby');
        },
        show() { if (isCurrent()) { if (!root.open) root.showModal(); close.focus(); } },
        release: options => release(owner, options)
      });
    },
    reset() { if (mounted) release(mounted); cancelFocusReturn(); },
    signedOut() { if (disposed) return; if (mounted) release(mounted); cancelFocusReturn(); },
    dispose() { if (mounted) release(mounted); disposed = true; events.abort(); cancelFocusReturn(); }
  };
}
