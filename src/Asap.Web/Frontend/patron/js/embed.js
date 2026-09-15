const RESIZE_MESSAGE_TYPE = 'asap:patron-resize';

export function isEmbedMode() {
  try {
    const params = new URLSearchParams(window.location.search || '');
    return params.get('embed') === '1' || params.get('embed') === 'true';
  } catch (err) {
    return false;
  }
}

function documentHeight() {
  const body = document.body;
  const root = document.documentElement;
  return Math.max(
    body ? body.scrollHeight : 0,
    body ? body.offsetHeight : 0,
    root ? root.scrollHeight : 0,
    root ? root.offsetHeight : 0
  );
}

export function postEmbedResize() {
  if (!isEmbedMode() || window.parent === window) return;
  window.parent.postMessage({
    type: RESIZE_MESSAGE_TYPE,
    height: documentHeight()
  }, '*');
}

export function initEmbedMode() {
  if (!isEmbedMode()) return;
  document.body.classList.add('asap-embed');
  postEmbedResize();
  window.addEventListener('load', postEmbedResize);
  window.addEventListener('resize', postEmbedResize);
  if (window.ResizeObserver) {
    const observer = new ResizeObserver(postEmbedResize);
    observer.observe(document.body);
  }
  setTimeout(postEmbedResize, 50);
  setTimeout(postEmbedResize, 250);
}
