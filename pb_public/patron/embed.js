(function () {
  const MESSAGE_TYPE = 'asap:patron-resize';
  const MIN_HEIGHT = 520;
  const MAX_HEIGHT = 1800;

  function clean(value) {
    return String(value || '').trim();
  }

  function originFromSrc(src) {
    try {
      return new URL(src, window.location.href).origin;
    } catch (err) {
      return '';
    }
  }

  function patronUrl(base, libraryOrgId) {
    const url = new URL('/patron/', base);
    if (libraryOrgId) url.searchParams.set('libraryOrgId', libraryOrgId);
    url.searchParams.set('embed', '1');
    return url.toString();
  }

  function createIframe(target) {
    const src = clean(target.getAttribute('data-src'));
    const libraryOrgId = clean(target.getAttribute('data-library-org-id'));
    if (!src || !libraryOrgId) return null;

    const iframe = document.createElement('iframe');
    iframe.src = patronUrl(src, libraryOrgId);
    iframe.title = clean(target.getAttribute('data-title')) || 'Suggest a purchase';
    iframe.loading = 'lazy';
    iframe.style.width = '100%';
    iframe.style.minHeight = MIN_HEIGHT + 'px';
    iframe.style.border = '0';
    iframe.style.display = 'block';
    iframe.setAttribute('data-asap-patron-frame', 'true');
    target.replaceChildren(iframe);
    return iframe;
  }

  const frames = [];
  document.querySelectorAll('[data-asap-suggestions]').forEach((target) => {
    const iframe = createIframe(target);
    if (!iframe) return;
    frames.push({
      iframe: iframe,
      origin: originFromSrc(iframe.src)
    });
  });

  window.addEventListener('message', (event) => {
    const data = event.data || {};
    if (!data || data.type !== MESSAGE_TYPE) return;
    const match = frames.find((frame) => frame.origin && frame.origin === event.origin);
    if (!match) return;
    const height = Math.min(MAX_HEIGHT, Math.max(MIN_HEIGHT, parseInt(data.height, 10) || MIN_HEIGHT));
    match.iframe.style.height = height + 'px';
  });
})();
