const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

// Flat history responses are paged even in controller fixtures. Keep the query
// identity and safe display labels explicit when testing other controller work.
exports.emailHistoryPage = (result, path) => {
  const query = new URL(path, 'https://localhost').searchParams;
  const scope = query.get('scope') || query.get('organizationId') || 'all';
  return { count: result.items.length, page: Number(query.get('page') || 1), pageSize: 25,
    scope: scope === '1' ? 'system' : scope, libraries: [], types: [], ...result,
    items: result.items.map(item => ({ typeLabel: item.deliveryClass, detailLabel: item.lastErrorCode || item.suppressionReason, ...item })) };
};

exports.fixture = async journey => {
  const frontend = path.join(__dirname, '../../src/Asap.Web/Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-controller-'));
  const globals = new Map(['window', 'document', 'Node', 'HTMLElement', 'URLSearchParams', 'FormData', 'Event', 'fetch']
    .map(name => [name, Object.getOwnPropertyDescriptor(global, name)]));
  let dom;
  try {
    dom = new JSDOM(fs.readFileSync(path.join(frontend, 'staff/index.html'), 'utf8'),
      { url: 'https://localhost/staff/', pretendToBeVisual: true });
    fs.cpSync(path.join(frontend, 'staff'), path.join(temporary, 'staff'), { recursive: true });
    fs.cpSync(path.join(frontend, 'shared'), path.join(temporary, 'shared'), { recursive: true });
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
    Object.assign(global, { window: dom.window, document: dom.window.document, Node: dom.window.Node,
      HTMLElement: dom.window.HTMLElement, URLSearchParams: dom.window.URLSearchParams,
      FormData: dom.window.FormData, Event: dom.window.Event });
    dom.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    dom.window.HTMLDialogElement.prototype.close = function () { this.open = false; };
    require('./confirmation.cjs').answerConfirmations(dom.window, () => true);
    const load = name => import(pathToFileURL(path.join(temporary, `staff/js/${name}.js`)).href);
    await journey({ dom, load, get: selector => dom.window.document.querySelector(selector) });
  } finally {
    dom?.window.close();
    for (const [name, descriptor] of globals) {
      if (descriptor) Object.defineProperty(global, name, descriptor);
      else delete global[name];
    }
    const resolved = path.resolve(temporary);
    if (!resolved.startsWith(path.resolve(os.tmpdir()) + path.sep)) throw new Error('Unsafe fixture cleanup');
    fs.rmSync(resolved, { recursive: true, force: true });
  }
};
