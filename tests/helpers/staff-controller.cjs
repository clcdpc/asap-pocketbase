const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

exports.fixture = async journey => {
  const frontend = path.join(__dirname, '../../src/Asap.Web/Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-controller-'));
  const dom = new JSDOM(fs.readFileSync(path.join(frontend, 'staff/index.html'), 'utf8'),
    { url: 'https://localhost/staff/', pretendToBeVisual: true });
  try {
    fs.cpSync(path.join(frontend, 'staff'), path.join(temporary, 'staff'), { recursive: true });
    fs.cpSync(path.join(frontend, 'shared'), path.join(temporary, 'shared'), { recursive: true });
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
    Object.assign(global, { window: dom.window, document: dom.window.document, Node: dom.window.Node,
      HTMLElement: dom.window.HTMLElement, URLSearchParams: dom.window.URLSearchParams });
    dom.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    dom.window.HTMLDialogElement.prototype.close = function () { this.open = false; };
    dom.window.confirm = () => true;
    const load = name => import(pathToFileURL(path.join(temporary, `staff/js/${name}.js`)).href);
    await journey({ dom, load, get: selector => dom.window.document.querySelector(selector) });
  } finally {
    dom.window.close();
    const resolved = path.resolve(temporary);
    if (!resolved.startsWith(path.resolve(os.tmpdir()) + path.sep)) throw new Error('Unsafe fixture cleanup');
    fs.rmSync(resolved, { recursive: true, force: true });
  }
};
